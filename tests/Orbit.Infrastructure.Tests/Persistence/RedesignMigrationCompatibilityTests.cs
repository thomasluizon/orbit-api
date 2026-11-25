using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class RedesignMigrationCompatibilityTests
{
    [Fact]
    public void RedesignModel_PreservesTheMainSchema()
    {
        using var context = CreateContext();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var baseline = initializer.Initialize(new AddMarketingContacts().TargetModel, designTime: true);
        var current = initializer.Initialize(new AddHabitSkipUndo().TargetModel, designTime: true);

        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(baseline.GetRelationalModel(), current.GetRelationalModel());

        differences.Should().NotBeEmpty();
        differences.Should().OnlyContain(operation =>
            operation is AddColumnOperation || operation is CreateTableOperation || operation is CreateIndexOperation);
        differences.OfType<AddColumnOperation>().Should().OnlyContain(column =>
            column.IsNullable || column.DefaultValue != null || column.DefaultValueSql != null);
        differences.OfType<CreateTableOperation>().Should().ContainSingle()
            .Which.Name.Should().Be("HabitSkipUndos");
        differences.OfType<CreateIndexOperation>().Should().OnlyContain(index => !index.IsUnique);
    }

    [Fact]
    public void RedesignMigrations_AllowLegacyInsertsAndHabitDeletion()
    {
        Migration[] migrations =
            [new AddPendingOperationRevision(), new AddClockFormatPreference(), new AddHabitSkipUndo()];
        var operations = migrations.SelectMany(migration => migration.UpOperations).ToArray();

        operations.Should().OnlyContain(operation =>
            operation is AddColumnOperation || operation is CreateTableOperation || operation is CreateIndexOperation);
        var columns = operations.OfType<AddColumnOperation>().ToArray();
        columns.Should().HaveCount(3);
        columns.Should().OnlyContain(column => column.IsNullable || column.DefaultValue != null);
        columns.Single(column => column.Name == "RevisionNumber").DefaultValue.Should().Be(0);

        var undoTable = operations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        undoTable.Name.Should().Be("HabitSkipUndos");
        var foreignKey = undoTable.ForeignKeys.Should().ContainSingle().Subject;
        foreignKey.PrincipalTable.Should().Be("Habits");
        foreignKey.OnDelete.Should().Be(ReferentialAction.Cascade);
    }

    [Fact]
    public void IdempotentUpgrade_IncludesEveryMissingMigrationRegardlessOfItsTimestamp()
    {
        using var context = CreateContext();
        var script = context.GetService<IMigrator>().GenerateScript(
            toMigration: LastCheckedMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);

        string[] missingMigrations =
        [
            "20260926145421_AddPendingOperationRevision",
            "20260929070016_LinkGamificationNotificationsToProgress",
            "20260929070408_AddClockFormatPreference",
            "20261001074038_AddHabitSkipUndo",
            LastCheckedMigration
        ];
        foreach (var migration in missingMigrations)
            script.Should().Contain(
                $"IF NOT EXISTS(SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{migration}')");
    }

    [Fact]
    public void IdempotentScript_CannotIncludeAMigrationThatCreatesAnIndexConcurrently()
    {
        using var context = CreateContext();
        var migrator = context.GetService<IMigrator>();
        var concurrentMigrations = context.GetService<IMigrationsAssembly>().Migrations
            .Where(entry => context.GetService<IMigrationsAssembly>()
                .CreateMigration(entry.Value, "Npgsql.EntityFrameworkCore.PostgreSQL").UpOperations
                .OfType<CreateIndexOperation>()
                .Any(index => index["Npgsql:CreatedConcurrently"] as bool? == true))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        concurrentMigrations.Should().NotBeEmpty();
        concurrentMigrations.Should().OnlyContain(id => string.CompareOrdinal(id, LastCheckedMigration) > 0,
            "the idempotent script ends before the first concurrent index migration");

        var generate = () => migrator.GenerateScript(
            toMigration: concurrentMigrations[0],
            options: MigrationsSqlGenerationOptions.Idempotent);
        generate.Should().Throw<NotSupportedException>().WithMessage("*CREATE INDEX CONCURRENTLY*");
    }

    private const string LastCheckedMigration = "20261017011939_AddFeatureFlagAccounts";

    private static OrbitDbContext CreateContext() => new(
        new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused")
            .Options);
}
