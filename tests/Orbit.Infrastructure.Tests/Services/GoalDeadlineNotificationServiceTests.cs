using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the FormatDeadlineBody logic for goal deadline notifications.
/// The background service loop and DB interactions are integration concerns.
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
        return Goal.Create(
            ValidUserId,
            "Run Marathon",
            targetValue,
            unit,
            deadline: deadline ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7)).Value;
    }

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
}
