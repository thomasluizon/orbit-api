using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AppStoreMigrationTests
{
    internal static IReadOnlyList<MigrationOperation> WithoutAppleAdditions(IReadOnlyList<MigrationOperation> operations)
    {
        var userColumns = new[] { "AppStoreOriginalTransactionId", "AppStoreEnvironment", "AppStoreStateSignedAtUtc",
            "AppleSubject", "AppleRefreshToken", "AppleTokenClientId" };
        var columns = operations.OfType<AddColumnOperation>().Where(column => column.Table == "Users" && userColumns.Contains(column.Name)).ToArray();
        columns.Select(column => column.Name).Should().BeEquivalentTo(userColumns);
        columns.Should().OnlyContain(column => column.IsNullable && !column.IsDestructiveChange);
        columns.Should().ContainSingle(column => column.Name == "AppStoreStateSignedAtUtc" && column.ClrType == typeof(DateTime));
        columns.Where(column => column.Name != "AppStoreStateSignedAtUtc").Should().OnlyContain(column => column.ClrType == typeof(string));
        var table = operations.OfType<CreateTableOperation>().Should().ContainSingle(operation => operation.Name == "ProcessedAppStoreNotifications").Subject;
        table.Columns.Select(column => column.Name).Should().BeEquivalentTo("Id", "NotificationUuid", "Environment", "ProcessedAtUtc");
        table.PrimaryKey!.Columns.Should().Equal("Id");
        table.ForeignKeys.Should().BeEmpty();
        var indexNames = new[] { "IX_Users_AppStoreOriginalTransactionId", "IX_Users_AppleSubject", "IX_ProcessedAppStoreNotifications_NotificationUuid" };
        var indexes = operations.OfType<CreateIndexOperation>().Where(index => indexNames.Contains(index.Name)).ToArray();
        indexes.Select(index => index.Name).Should().BeEquivalentTo(indexNames);
        indexes.Should().OnlyContain(index => index.IsUnique);
        indexes.Where(index => index.Table == "Users").Should().HaveCount(2).And.OnlyContain(index => index.Filter != null);
        var coverageColumns = operations.OfType<AddColumnOperation>()
            .Where(column => column.Table == "Users" && column.Name.Contains("Coverage", StringComparison.Ordinal)).ToArray();
        coverageColumns.Select(column => column.Name).Should().BeEquivalentTo(
            new[] { "Stripe", "Play", "AppStore" }.SelectMany(provider =>
                new[] { $"{provider}CoverageExpiresAtUtc", $"{provider}CoverageInterval", $"{provider}CoverageLapseReason" }));
        coverageColumns.Should().OnlyContain(column => column.IsNullable && !column.IsDestructiveChange);
        var additions = columns.Cast<MigrationOperation>().Concat(coverageColumns).Append(table).Concat(indexes).ToHashSet();
        additions.Should().HaveCount(19);
        return operations.Where(operation => !additions.Contains(operation)).ToArray();
    }

    [Fact]
    public void MigrationAndSnapshot_AddNullableBindingsAndUniqueNotificationLedger()
    {
        var migration = new AddAppStoreSubscriptions();
        migration.UpOperations.OfType<AddColumnOperation>().Should().HaveCount(3).And.OnlyContain(column => column.IsNullable);
        migration.UpOperations.Should().NotContain(operation => operation is DropColumnOperation || operation is DropTableOperation || operation is AlterColumnOperation);
        migration.UpOperations.OfType<CreateIndexOperation>().Should().Contain(index => index.Name == "IX_Users_AppStoreOriginalTransactionId" && index.IsUnique);
        var snapshot = new OrbitDbContextModelSnapshot().Model;
        var user = snapshot.FindEntityType(typeof(User).FullName!)!;
        user.FindProperty(nameof(User.AppStoreOriginalTransactionId))!.IsNullable.Should().BeTrue();
        snapshot.FindEntityType(typeof(ProcessedAppStoreNotification).FullName!)!.GetIndexes().Should().Contain(index => index.IsUnique);

        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var migrations = context.GetService<IMigrationsAssembly>().Migrations;
        Migration[] appleMigrations = [migration, new AddAppleSignInCredentials(), new PreserveProviderPaidCoverage()];
        var mainId = migrations.Single(pair => pair.Value.AsType() == typeof(ExpandHabitNextDueDate)).Key;
        var appleIds = appleMigrations.Select(apple =>
            migrations.Single(pair => pair.Value.AsType() == apple.GetType()).Key).ToArray();
        migrations.Keys.Should().ContainInOrder(new[] { mainId }.Concat(appleIds));
        migrations.Keys.Last().Should().EndWith("_DropSentProactiveCheckins");

        foreach (var apple in appleMigrations)
        {
            var habit = apple.TargetModel.FindEntityType(typeof(Habit).FullName!)!;
            habit.FindProperty(nameof(Habit.NextDueDate)).Should().NotBeNull();
            habit.FindProperty("DueDate")!.ValueGenerated.Should().Be(ValueGenerated.OnAddOrUpdate);
        }

        var latest = context.GetService<IModelRuntimeInitializer>()
            .Initialize(appleMigrations.Last().TargetModel, designTime: true);
        var current = context.GetService<IDesignTimeModel>().Model;
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(latest.GetRelationalModel(), current.GetRelationalModel());
        differences.Should().ContainSingle();
        DropSentProactiveCheckinsMigrationTests.AssertRetiredLedgerDrop(differences);
    }
}
