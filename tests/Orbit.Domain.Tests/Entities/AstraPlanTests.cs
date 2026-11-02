using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public class AstraPlanTests
{
    [Theory]
    [InlineData("done", true)]
    [InlineData("failed", true)]
    [InlineData("skipped", true)]
    [InlineData("unknown", false)]
    public void Recording_RejectsInvalidPlanStatus(string status, bool valid)
    {
        var plan = new AstraPlan(Guid.NewGuid(), "Your progress", [new("1", "Read habits", status, "query_habits", "execution"),
            new("2", "Read goals", "done", "query_goals", "execution")]);
        var response = JsonSerializer.Serialize(new { plan }, AstraPlan.JsonOptions);
        var recorded = ConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), "assistant", DateTime.UtcNow, null, null, "[]", response);
        recorded.IsSuccess.Should().Be(valid);
    }

    [Theory]
    [InlineData("query_habits", false)]
    [InlineData("Read\nhabits", false)]
    [InlineData("Read habits", true)]
    public void PlanFactory_GuardsPersonFacingText(string text, bool valid)
    {
        AstraPlan.Create("Your progress", [new("1", text, "pending", "query_habits", "execution"),
            new("2", "Read goals", "pending", "query_goals", "execution")]).IsSuccess.Should().Be(valid);
    }
}
