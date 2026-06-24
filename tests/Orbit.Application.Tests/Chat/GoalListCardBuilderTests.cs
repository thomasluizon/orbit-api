using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Chat;

public class GoalListCardBuilderTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static Goal CreateGoal(
        string title, decimal target, string unit, decimal current = 0, int position = 0, DateOnly? deadline = null)
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, title, target, unit, Deadline: deadline, Position: position)).Value;
        if (current > 0)
            goal.UpdateProgress(current);
        return goal;
    }

    [Fact]
    public void TryExtractDirective_NoDirective_ReturnsFalse()
    {
        var found = GoalListCardBuilder.TryExtractDirective("Here are your goals.", out var stripped);

        found.Should().BeFalse();
        stripped.Should().Be("Here are your goals.");
    }

    [Fact]
    public void TryExtractDirective_Directive_ReturnsTrueAndStripsToken()
    {
        var found = GoalListCardBuilder.TryExtractDirective("Your goals:\n[[orbit:goals]]", out var stripped);

        found.Should().BeTrue();
        stripped.Should().Be("Your goals:");
    }

    [Fact]
    public void TryExtractDirective_IsCaseInsensitive()
    {
        GoalListCardBuilder.TryExtractDirective("[[ORBIT:GOALS]]", out _).Should().BeTrue();
    }

    [Fact]
    public void Build_MapsProgressAndPreservesPositionOrder()
    {
        var second = CreateGoal("Run 100km", 100, "km", current: 40, position: 1, deadline: new DateOnly(2026, 12, 31));
        var first = CreateGoal("Read 12 books", 12, "books", current: 5, position: 0);

        var card = GoalListCardBuilder.Build([second, first]);

        card.Items.Select(item => item.Title).Should().ContainInOrder("Read 12 books", "Run 100km");
        var run = card.Items.Single(item => item.Title == "Run 100km");
        run.Current.Should().Be(40);
        run.Target.Should().Be(100);
        run.Unit.Should().Be("km");
        run.Deadline.Should().Be("2026-12-31");
    }

    [Fact]
    public void Build_NoDeadline_LeavesDeadlineNull()
    {
        var goal = CreateGoal("Meditate 30 days", 30, "days", current: 12);

        var card = GoalListCardBuilder.Build([goal]);

        card.Items.Single().Deadline.Should().BeNull();
    }
}
