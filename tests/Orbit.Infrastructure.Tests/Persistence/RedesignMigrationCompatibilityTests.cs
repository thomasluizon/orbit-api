using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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
        var current = context.GetService<IDesignTimeModel>().Model;

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
            options: MigrationsSqlGenerationOptions.Idempotent);

        string[] missingMigrations =
        [
            "20260926145421_AddPendingOperationRevision",
            "20260929070016_LinkGamificationNotificationsToProgress",
            "20260929070408_AddClockFormatPreference",
            "20261001074038_AddHabitSkipUndo"
        ];
        foreach (var migration in missingMigrations)
            script.Should().Contain(
                $"IF NOT EXISTS(SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{migration}')");
    }

    private static OrbitDbContext CreateContext() => new(
        new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused")
            .Options);
}
