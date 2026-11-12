using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class PhraseReaderMigrationTests
{
    private static IReadOnlyList<MigrationOperation> Differences()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new AddPendingOperationPreviewRevised().TargetModel, designTime: true);
        var current = initializer.Initialize(new AddPhraseReaderAndMascotMotion().TargetModel, designTime: true);
        return context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(previous.GetRelationalModel(), current.GetRelationalModel());
    }

    [Fact]
    public void Migration_AddsTheCounterAndThePreferenceToUsers()
    {
        var columns = Differences().OfType<AddColumnOperation>().ToArray();

        columns.Should().HaveCount(3).And.OnlyContain(column => column.Table == "Users");
        var motion = columns.Should().ContainSingle(column => column.Name == "MascotMotionEnabled").Subject;
        motion.IsNullable.Should().BeFalse();
        motion.DefaultValue.Should().Be(true);
        var count = columns.Should().ContainSingle(column => column.Name == "PhraseReadsToday").Subject;
        count.IsNullable.Should().BeFalse();
        count.DefaultValue.Should().Be(0);
        columns.Should().ContainSingle(column => column.Name == "PhraseReadsLocalDate").Which.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void Migration_SeedsBothFlagRowsOffAndNoCapRow()
    {
        var differences = Differences();

        var seed = differences.OfType<InsertDataOperation>().Should().ContainSingle().Subject;
        seed.Table.Should().Be("AppFeatureFlags");
        var enabled = Array.IndexOf(seed.Columns, "Enabled");
        var key = Array.IndexOf(seed.Columns, "Key");
        var rows = Enumerable.Range(0, seed.Values.GetLength(0))
            .ToDictionary(row => (string)seed.Values[row, key]!, row => (bool)seed.Values[row, enabled]!);
        rows.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["phrase_reader_ai_disabled"] = false,
            ["orbit_guided_flows"] = false,
        });
        differences.OfType<InsertDataOperation>().Should().NotContain(operation => operation.Table == "AppConfigs");
    }
}
