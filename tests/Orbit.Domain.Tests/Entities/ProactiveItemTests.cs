using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public sealed class ProactiveItemTests
{
    private static readonly DateTime Now = new(2030, 10, 14, 19, 0, 0, DateTimeKind.Utc);
    private static ProactiveItem Create(string text = "Text.") => ProactiveItem.Create(Guid.NewGuid(),
        ProactiveTrigger.EveningCheckin, new(2030, 10, 14), "{}", text, "Action", "Prompt", Now);

    [Fact]
    public void LifecycleEvaluation_DoesNotChangeTimestamps()
    {
        var item = Create();
        item.Show(Now);
        item.EvaluateEndReason(new(2030, 10, 14), 19, true).Should().BeNull();
        item.EvaluateEndReason(new(2030, 10, 14), 19, false).Should().Be(ProactiveEndReason.ConditionResolved);
        item.EvaluateEndReason(new(2030, 10, 14), 22, true).Should().Be(ProactiveEndReason.WindowClosed);
        item.EvaluateEndReason(new(2030, 10, 15), 19, true).Should().Be(ProactiveEndReason.DayEnded);
        item.ShownAtUtc.Should().Be(Now);
        item.OpenedAtUtc.Should().BeNull();
        item.ActedAtUtc.Should().BeNull();
        item.EndedAtUtc.Should().BeNull();
        item.EndReason.Should().BeNull();
        item.Act(Now);
        item.EvaluateEndReason(new(2030, 10, 14), 19, true).Should().Be(ProactiveEndReason.Acted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void LifecycleEvaluation_RejectsInvalidLocalHour(int hour)
    {
        Action evaluate = () => Create().EvaluateEndReason(new(2030, 10, 14), hour, true);
        evaluate.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LifecycleEvaluation_RejectsMissingLocalDate()
    {
        Action evaluate = () => Create().EvaluateEndReason(default, 19, true);
        evaluate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FirstTimes_AreIdempotentAndConditionResolutionCountsAsActedOnlyAfterExposure()
    {
        var item = Create();
        item.Show(Now);
        item.Show(Now.AddMinutes(1));
        item.Push(Now);
        item.Open(Now.AddMinutes(2));
        item.Open(Now.AddMinutes(3));
        item.End(ProactiveEndReason.ConditionResolved, Now.AddMinutes(4)).Should().BeTrue();
        item.End(ProactiveEndReason.WindowClosed, Now.AddMinutes(5)).Should().BeFalse();
        item.ShownAtUtc.Should().Be(Now);
        item.OpenedAtUtc.Should().Be(Now.AddMinutes(2));
        item.ActedAtUtc.Should().Be(Now.AddMinutes(4));
        item.Ignored.Should().BeFalse();
        var unseen = Create();
        unseen.End(ProactiveEndReason.ConditionResolved, Now);
        unseen.ActedAtUtc.Should().BeNull();
        unseen.Ignored.Should().BeFalse();
    }

    [Fact]
    public void ExposureWithoutEngagement_IsIgnoredWhenEnded()
    {
        var item = Create();
        item.Push(Now);
        item.End(ProactiveEndReason.WindowClosed, Now);
        item.Ignored.Should().BeTrue();
        item.Open(Now);
        item.Ignored.Should().BeFalse();
    }

    [Fact]
    public void FactoryAndMutators_RejectInvalidState()
    {
        Action empty = () => Create("");
        Action longText = () => Create(new string('x', 141));
        Action invalidOwner = () => ProactiveItem.Create(Guid.Empty, ProactiveTrigger.MorningPlan,
            new(2030, 10, 14), "{}", "Text", "Action", "Prompt", Now);
        Action invalidFacts = () => ProactiveItem.Create(Guid.NewGuid(), ProactiveTrigger.MorningPlan,
            new(2030, 10, 14), "[]", "Text", "Action", "Prompt", Now);
        Action noGoal = () => ProactiveItem.Create(Guid.NewGuid(), ProactiveTrigger.GoalBehind,
            new(2030, 10, 14), "{}", "Text", "Action", "Prompt", Now);
        Action localTimestamp = () => Create().Show(DateTime.SpecifyKind(Now, DateTimeKind.Unspecified));
        foreach (var action in new[] { empty, longText, invalidOwner, invalidFacts, noGoal, localTimestamp })
            action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void EffectivePushSetting_RequiresProAccess(bool? choice, bool expected)
    {
        var user = User.Create("Person", "person@example.com").Value;
        if (choice.HasValue) user.SetProactiveAstraEnabled(choice.Value);
        user.GrantLifetimePro();
        user.EffectiveProactivePushEnabled.Should().Be(expected);
        user.ProactiveAstraEnabled.Should().Be(choice ?? true);
        var free = User.Create("Free", "free@example.com").Value;
        free.StartTrial(DateTime.UtcNow.AddDays(-1));
        free.SetProactiveAstraEnabled(true);
        free.EffectiveProactivePushEnabled.Should().BeFalse();
    }

    [Fact]
    public void CalendarClaim_GuardsInvalidCounts()
    {
        Action owner = () => ProactiveCalendarDay.Claim(Guid.Empty, new(2030, 10, 14));
        Action count = () => ProactiveCalendarDay.Claim(Guid.NewGuid(), new(2030, 10, 14)).Complete(-1, 1);
        owner.Should().Throw<ArgumentException>();
        count.Should().Throw<ArgumentOutOfRangeException>();
        ProactiveRules.IsQuiet(7).Should().BeTrue();
        ProactiveRules.IsQuiet(8).Should().BeFalse();
    }
}
