using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class MakeSubHabitsFreeMigrationTests
{
    [Fact]
    public void Up_OnlyFreesFeatureAndDeletesObsoleteGateConfig()
    {
        var operations = new MakeSubHabitsFree().UpOperations;

        operations.Should().HaveCount(2);
        var feature = operations.OfType<UpdateDataOperation>().Should().ContainSingle().Subject;
        feature.Table.Should().Be("AppFeatureFlags");
        feature.KeyColumns.Should().Equal("Key");
        feature.KeyValues[0, 0].Should().Be("sub_habits");
        feature.Columns.Should().Equal("PlanRequirement");
        feature.Values[0, 0].Should().BeNull();
        var config = operations.OfType<DeleteDataOperation>().Should().ContainSingle().Subject;
        config.Table.Should().Be("AppConfigs");
        config.KeyColumns.Should().Equal("Key");
        config.KeyValues[0, 0].Should().Be("SubHabitsProOnly");
    }

    [Fact]
    public void Down_RestoresFeaturePlanAndOriginalGateConfig()
    {
        var operations = new MakeSubHabitsFree().DownOperations;

        operations.Should().HaveCount(2);
        var feature = operations.OfType<UpdateDataOperation>().Should().ContainSingle().Subject;
        feature.Table.Should().Be("AppFeatureFlags");
        feature.KeyColumns.Should().Equal("Key");
        feature.KeyValues[0, 0].Should().Be("sub_habits");
        feature.Columns.Should().Equal("PlanRequirement");
        feature.Values[0, 0].Should().Be("Pro");
        var config = operations.OfType<InsertDataOperation>().Should().ContainSingle().Subject;
        config.Table.Should().Be("AppConfigs");
        config.Columns.Should().Equal("Key", "Description", "Value");
        config.Values.Cast<object>().Should().Equal("SubHabitsProOnly",
            "Whether sub-habit creation is restricted to Pro users", "true");
    }
}
