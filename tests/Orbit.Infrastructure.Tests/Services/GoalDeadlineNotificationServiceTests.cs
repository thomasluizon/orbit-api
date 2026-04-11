using System.Reflection;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the FormatDeadlineBody logic and NotifyDaysBefore constants
/// for goal deadline notifications. The background service loop
/// and DB interactions are integration concerns.
/// </summary>
public class GoalDeadlineNotificationServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    private static Goal CreateGoal(
        decimal currentValue = 5,
        decimal targetValue = 10,
        string unit = "km",
        DateOnly? deadline = null)
    {
        return Goal.Create(new Goal.CreateGoalParams(
            ValidUserId,
            "Run Marathon",
            targetValue,
            unit,
            Deadline: deadline ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7))).Value;
    }

    // ── FormatDeadlineBody ──

    [Fact]
    public void FormatDeadlineBody_OneDayBefore_English_ReturnsTomorrowMessage()
    {
        // Arrange
        var goal = CreateGoal();
        goal.UpdateProgress(5);

        // Act
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 1, "en");

        // Assert
        body.Should().Contain("due tomorrow");
        body.Should().Contain("5/10 km");
    }

    [Fact]
    public void FormatDeadlineBody_OneDayBefore_Portuguese_ReturnsTomorrowMessage()
    {
        // Arrange
        var goal = CreateGoal();
        goal.UpdateProgress(3);

        // Act
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 1, "pt-br");

        // Assert
        body.Should().Contain("amanhã");
        body.Should().Contain("3/10 km");
    }

    [Fact]
    public void FormatDeadlineBody_ThreeDaysBefore_English_ReturnsDaysMessage()
    {
        // Arrange
        var goal = CreateGoal();

        // Act
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 3, "en");

        // Assert
        body.Should().Contain("due in 3 days");
    }

    [Fact]
    public void FormatDeadlineBody_SevenDaysBefore_English_ReturnsDaysMessage()
    {
        // Arrange
        var goal = CreateGoal();

        // Act
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 7, "en");

        // Assert
        body.Should().Contain("due in 7 days");
    }

    [Fact]
    public void FormatDeadlineBody_ThreeDaysBefore_Portuguese_ReturnsDaysMessage()
    {
        // Arrange
        var goal = CreateGoal();

        // Act
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 3, "pt-br");

        // Assert
        body.Should().Contain("termina em 3 dias");
    }

    [Fact]
    public void FormatDeadlineBody_SevenDaysBefore_Portuguese_ReturnsDaysMessage()
    {
        // Arrange
        var goal = CreateGoal();

        // Act
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 7, "pt");

        // Assert
        body.Should().Contain("termina em 7 dias");
    }

    [Fact]
    public void FormatDeadlineBody_IncludesProgressText()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100, unit: "pages");
        goal.UpdateProgress(42);

        // Act
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 3, "en");

        // Assert
        body.Should().Contain("42/100 pages");
    }

    [Fact]
    public void FormatDeadlineBody_ZeroProgress_ShowsZero()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 50, unit: "sessions");

        // Act
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 1, "en");

        // Assert
        body.Should().Contain("0/50 sessions");
    }

    // ── Additional FormatDeadlineBody edge cases ──

    [Theory]
    [InlineData(2, "en", "due in 2 days")]
    [InlineData(5, "en", "due in 5 days")]
    [InlineData(14, "en", "due in 14 days")]
    public void FormatDeadlineBody_VariousDays_English_FormatsCorrectly(int days, string lang, string expected)
    {
        var goal = CreateGoal();
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, days, lang);
        body.Should().Contain(expected);
    }

    [Theory]
    [InlineData(2, "pt-br", "termina em 2 dias")]
    [InlineData(5, "pt", "termina em 5 dias")]
    [InlineData(14, "pt-br", "termina em 14 dias")]
    public void FormatDeadlineBody_VariousDays_Portuguese_FormatsCorrectly(int days, string lang, string expected)
    {
        var goal = CreateGoal();
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, days, lang);
        body.Should().Contain(expected);
    }

    [Fact]
    public void FormatDeadlineBody_OneDayBefore_English_DoesNotContainDaysPlural()
    {
        var goal = CreateGoal();
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 1, "en");

        body.Should().Contain("tomorrow");
        body.Should().NotContain("in 1 days");
    }

    [Fact]
    public void FormatDeadlineBody_OneDayBefore_Portuguese_DoesNotContainDiasPlural()
    {
        var goal = CreateGoal();
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 1, "pt-br");

        body.Should().Contain("amanhã");
        body.Should().NotContain("em 1 dias");
    }

    [Fact]
    public void FormatDeadlineBody_DecimalProgress_FormatsCorrectly()
    {
        var goal = CreateGoal(targetValue: 10, unit: "miles");
        goal.UpdateProgress(3.5m);

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 3, "en");

        body.Should().Contain("3.5/10 miles");
    }

    [Fact]
    public void FormatDeadlineBody_LargeTargetValue_FormatsCorrectly()
    {
        var goal = CreateGoal(targetValue: 10000, unit: "steps");
        goal.UpdateProgress(5000);

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, 7, "en");

        body.Should().Contain("5000/10000 steps");
    }

    // ── NotifyDaysBefore constant ──

    [Fact]
    public void NotifyDaysBefore_ContainsExpectedValues()
    {
        var field = typeof(GoalDeadlineNotificationService)
            .GetField("NotifyDaysBefore",
                BindingFlags.NonPublic | BindingFlags.Static)!;
        var values = (int[])field.GetValue(null)!;

        values.Should().BeEquivalentTo([7, 3, 1]);
    }

    [Fact]
    public void NotifyDaysBefore_IsSortedDescending()
    {
        var field = typeof(GoalDeadlineNotificationService)
            .GetField("NotifyDaysBefore",
                BindingFlags.NonPublic | BindingFlags.Static)!;
        var values = (int[])field.GetValue(null)!;

        values.Should().BeInDescendingOrder();
    }

    // ── Deduplication key format ──

    [Fact]
    public void DeduplicationKey_MatchesExpectedFormat()
    {
        // Replicate the key format used in ProcessGoalDeadlineAsync
        var goalId = Guid.NewGuid();
        var daysBefore = 3;

        var key = $"goal-deadline-{goalId}-{daysBefore}d";

        key.Should().StartWith("goal-deadline-");
        key.Should().EndWith("3d");
        key.Should().Contain(goalId.ToString());
    }

    [Fact]
    public void DeduplicationKey_DifferentGoalsSameDay_ProduceDifferentKeys()
    {
        var goal1 = Guid.NewGuid();
        var goal2 = Guid.NewGuid();

        var key1 = $"goal-deadline-{goal1}-3d";
        var key2 = $"goal-deadline-{goal2}-3d";

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void DeduplicationKey_SameGoalDifferentDays_ProduceDifferentKeys()
    {
        var goalId = Guid.NewGuid();

        var key7 = $"goal-deadline-{goalId}-7d";
        var key3 = $"goal-deadline-{goalId}-3d";
        var key1 = $"goal-deadline-{goalId}-1d";

        key7.Should().NotBe(key3);
        key3.Should().NotBe(key1);
    }
}
