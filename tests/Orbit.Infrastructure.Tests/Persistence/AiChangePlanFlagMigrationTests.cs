using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Application.Common;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AiChangePlanFlagMigrationTests
{
    private static IReadOnlyList<MigrationOperation> Differences(bool down = false)
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new AddPhraseReaderAndMascotMotion().TargetModel, designTime: true);
        var current = initializer.Initialize(new AddAiChangePlanFlag().TargetModel, designTime: true);
        var differ = context.GetService<IMigrationsModelDiffer>();
        return down
            ? differ.GetDifferences(current.GetRelationalModel(), previous.GetRelationalModel())
            : differ.GetDifferences(previous.GetRelationalModel(), current.GetRelationalModel());
    }

    [Fact]
    public void Migration_InsertsOneFlagRowThatIsOffAndChangesNoOtherTable()
    {
        var differences = Differences();

        differences.Should().ContainSingle();
        var seed = differences.OfType<InsertDataOperation>().Should().ContainSingle().Subject;
        seed.Table.Should().Be("AppFeatureFlags");
        seed.Values.GetLength(0).Should().Be(1);
        seed.Values[0, Array.IndexOf(seed.Columns, "Key")].Should().Be(FeatureFlagKeys.AiChangePlan);
        seed.Values[0, Array.IndexOf(seed.Columns, "Enabled")].Should().Be(false);
        seed.Values[0, Array.IndexOf(seed.Columns, "PlanRequirement")].Should().BeNull();
    }

    [Fact]
    public void DownMigration_RemovesTheFlagRow()
    {
        var removal = Differences(down: true).Should().ContainSingle().Which.Should().BeOfType<DeleteDataOperation>().Subject;

        removal.Table.Should().Be("AppFeatureFlags");
        removal.KeyValues[0, 0].Should().Be(FeatureFlagKeys.AiChangePlan);
    }

    [Fact]
    public void HandWrittenMigration_MatchesTheModelDifference()
    {
        var up = new MigrationOperationRecorder(new AddAiChangePlanFlag()).Up();
        up.OfType<InsertDataOperation>().Should().ContainSingle()
            .Which.Values[0, 0].Should().Be(FeatureFlagKeys.AiChangePlan);
        up.OfType<SqlOperation>().Should().ContainSingle().Which.Sql.Should().Contain("lock_timeout");

        var down = new MigrationOperationRecorder(new AddAiChangePlanFlag()).Down();
        down.OfType<DeleteDataOperation>().Should().ContainSingle()
            .Which.KeyValues[0, 0].Should().Be(FeatureFlagKeys.AiChangePlan);
    }

    private sealed class MigrationOperationRecorder(Migration migration)
    {
        public IReadOnlyList<MigrationOperation> Up() => migration.UpOperations;

        public IReadOnlyList<MigrationOperation> Down() => migration.DownOperations;
    }
}
