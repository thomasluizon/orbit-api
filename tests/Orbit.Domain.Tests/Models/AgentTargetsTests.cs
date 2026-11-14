using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public sealed class AgentTargetsTests
{
    [Fact]
    public void Collect_ReadsTheRootAndTheContainerFieldsOnly()
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            habit_id = "root-habit",
            title = "Water",
            updates = new { goal_id = "inside-a-change" },
            filter = new { habit_ids = new[] { "filter-habit" } },
            habits = new[] { new { sub_habits = new[] { new { parent_habit_id = "nested-parent", tag_ids = new[] { "nested-tag" } } } } },
            positions = new[] { new { goal_id = "position-goal" } }
        });

        var fields = AgentTargets.Collect(arguments);

        fields.Select(field => (field.Resource.Name, field.Name, field.AtRoot)).Should().Equal(
            ("habit", "habit_id", true),
            ("habit", "habit_ids", false),
            ("habit", "parent_habit_id", false),
            ("tag", "tag_ids", false),
            ("goal", "goal_id", false));
        fields.Should().NotContain(field => field.Value.ToString() == "inside-a-change");
    }

    [Fact]
    public void Collect_ReadsEachObjectOfARootArrayAsNested()
    {
        var arguments = JsonSerializer.SerializeToElement(new[] { new { key_id = "first" }, new { key_id = "second" } });

        AgentTargets.Collect(arguments).Should().HaveCount(2).And.OnlyContain(field => field.Resource == AgentTargets.ApiKey && !field.AtRoot);
    }

    [Fact]
    public void Catalog_GivesEachTargetFieldOneResource()
    {
        var fields = AgentTargets.Resources.SelectMany(resource => resource.Fields).ToArray();

        fields.Should().OnlyHaveUniqueItems();
        fields.Should().OnlyContain(field => AgentTargets.IsTargetField(field));
        AgentTargets.IsTargetField("title").Should().BeFalse();
        AgentTargets.IsTargetField("filter").Should().BeFalse("a container holds target fields and is not one");
    }
}
