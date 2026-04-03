using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class GoalTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    private static Goal CreateValidGoal(
        decimal targetValue = 100,
        string unit = "pages",
        DateOnly? deadline = null)
    {
        var result = Goal.Create(ValidUserId, "Read 100 pages", targetValue, unit,
            description: "Reading goal", deadline: deadline);
        return result.Value;
    }

    // --- Create tests ---

    [Fact]
    public void Create_ValidInput_ReturnsSuccess()
    {
        var result = Goal.Create(ValidUserId, "Read 100 pages", 100, "pages",
            description: "Reading goal", deadline: new DateOnly(2026, 12, 31));

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(ValidUserId);
        result.Value.Title.Should().Be("Read 100 pages");
        result.Value.Description.Should().Be("Reading goal");
        result.Value.TargetValue.Should().Be(100);
        result.Value.CurrentValue.Should().Be(0);
        result.Value.Unit.Should().Be("pages");
        result.Value.Status.Should().Be(GoalStatus.Active);
        result.Value.Deadline.Should().Be(new DateOnly(2026, 12, 31));
        result.Value.Position.Should().Be(0);
        result.Value.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Create_MinimalInput_ReturnsSuccess()
    {
        var result = Goal.Create(ValidUserId, "Run", 10, "km");

        result.IsSuccess.Should().BeTrue();
        result.Value.Description.Should().BeNull();
        result.Value.Deadline.Should().BeNull();
        result.Value.Position.Should().Be(0);
    }

    [Fact]
    public void Create_EmptyUserId_ReturnsFailure()
    {
        var result = Goal.Create(Guid.Empty, "Read", 100, "pages");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User ID is required");
    }

    [Fact]
    public void Create_EmptyTitle_ReturnsFailure()
    {
        var result = Goal.Create(ValidUserId, "", 100, "pages");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Create_WhitespaceTitle_ReturnsFailure()
    {
        var result = Goal.Create(ValidUserId, "   ", 100, "pages");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Create_ZeroTargetValue_ReturnsFailure()
    {
        var result = Goal.Create(ValidUserId, "Read", 0, "pages");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value must be greater than 0");
    }

    [Fact]
    public void Create_NegativeTargetValue_ReturnsFailure()
    {
        var result = Goal.Create(ValidUserId, "Read", -10, "pages");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value must be greater than 0");
    }

    [Fact]
    public void Create_EmptyUnit_ReturnsFailure()
    {
        var result = Goal.Create(ValidUserId, "Read", 100, "");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Unit is required");
    }

    [Fact]
    public void Create_WhitespaceUnit_ReturnsFailure()
    {
        var result = Goal.Create(ValidUserId, "Read", 100, "   ");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Unit is required");
    }

    [Fact]
    public void Create_TrimsTitle()
    {
        var result = Goal.Create(ValidUserId, "  Read 100 pages  ", 100, "pages");

        result.Value.Title.Should().Be("Read 100 pages");
    }

    [Fact]
    public void Create_TrimsDescription()
    {
        var result = Goal.Create(ValidUserId, "Read", 100, "pages", description: "  My goal  ");

        result.Value.Description.Should().Be("My goal");
    }

    [Fact]
    public void Create_TrimsUnit()
    {
        var result = Goal.Create(ValidUserId, "Read", 100, "  pages  ");

        result.Value.Unit.Should().Be("pages");
    }

    [Fact]
    public void Create_NullDescription_SetsNull()
    {
        var result = Goal.Create(ValidUserId, "Read", 100, "pages", description: null);

        result.Value.Description.Should().BeNull();
    }

    [Fact]
    public void Create_WithPosition_SetsPosition()
    {
        var result = Goal.Create(ValidUserId, "Read", 100, "pages", position: 5);

        result.Value.Position.Should().Be(5);
    }

    [Fact]
    public void Create_SetsCreatedAtUtc()
    {
        var before = DateTime.UtcNow;

        var result = Goal.Create(ValidUserId, "Read", 100, "pages");

        var after = DateTime.UtcNow;
        result.Value.CreatedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Create_ProgressLogsStartEmpty()
    {
        var goal = CreateValidGoal();

        goal.ProgressLogs.Should().BeEmpty();
    }

    [Fact]
    public void Create_HabitsStartEmpty()
    {
        var goal = CreateValidGoal();

        goal.Habits.Should().BeEmpty();
    }

    // --- UpdateProgress tests ---

    [Fact]
    public void UpdateProgress_ActiveGoal_UpdatesCurrentValue()
    {
        var goal = CreateValidGoal(targetValue: 100);

        var result = goal.UpdateProgress(50);

        result.IsSuccess.Should().BeTrue();
        goal.CurrentValue.Should().Be(50);
        goal.Status.Should().Be(GoalStatus.Active);
    }

    [Fact]
    public void UpdateProgress_ReachesTarget_AutoCompletes()
    {
        var goal = CreateValidGoal(targetValue: 100);

        var result = goal.UpdateProgress(100);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Completed);
        goal.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void UpdateProgress_ExceedsTarget_AutoCompletes()
    {
        var goal = CreateValidGoal(targetValue: 100);

        var result = goal.UpdateProgress(150);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Completed);
        goal.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void UpdateProgress_NegativeValue_ReturnsFailure()
    {
        var goal = CreateValidGoal();

        var result = goal.UpdateProgress(-5);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Progress value cannot be negative");
    }

    [Fact]
    public void UpdateProgress_ZeroValue_Succeeds()
    {
        var goal = CreateValidGoal();

        var result = goal.UpdateProgress(0);

        result.IsSuccess.Should().BeTrue();
        goal.CurrentValue.Should().Be(0);
    }

    [Fact]
    public void UpdateProgress_CompletedGoal_ReturnsFailure()
    {
        var goal = CreateValidGoal(targetValue: 100);
        goal.MarkCompleted();

        var result = goal.UpdateProgress(50);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Cannot update progress on a non-active goal");
    }

    [Fact]
    public void UpdateProgress_AbandonedGoal_ReturnsFailure()
    {
        var goal = CreateValidGoal();
        goal.MarkAbandoned();

        var result = goal.UpdateProgress(50);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Cannot update progress on a non-active goal");
    }

    // --- Update tests ---

    [Fact]
    public void Update_ValidInput_UpdatesAllFields()
    {
        var goal = CreateValidGoal();

        var result = goal.Update("New Title", "New Description", 200, "km",
            new DateOnly(2027, 6, 30));

        result.IsSuccess.Should().BeTrue();
        goal.Title.Should().Be("New Title");
        goal.Description.Should().Be("New Description");
        goal.TargetValue.Should().Be(200);
        goal.Unit.Should().Be("km");
        goal.Deadline.Should().Be(new DateOnly(2027, 6, 30));
    }

    [Fact]
    public void Update_EmptyTitle_ReturnsFailure()
    {
        var goal = CreateValidGoal();

        var result = goal.Update("", "desc", 100, "pages", null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Update_WhitespaceTitle_ReturnsFailure()
    {
        var goal = CreateValidGoal();

        var result = goal.Update("   ", "desc", 100, "pages", null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Update_ZeroTargetValue_ReturnsFailure()
    {
        var goal = CreateValidGoal();

        var result = goal.Update("Title", "desc", 0, "pages", null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value must be greater than 0");
    }

    [Fact]
    public void Update_NegativeTargetValue_ReturnsFailure()
    {
        var goal = CreateValidGoal();

        var result = goal.Update("Title", "desc", -5, "pages", null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value must be greater than 0");
    }

    [Fact]
    public void Update_EmptyUnit_ReturnsFailure()
    {
        var goal = CreateValidGoal();

        var result = goal.Update("Title", "desc", 100, "", null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Unit is required");
    }

    [Fact]
    public void Update_TrimsFields()
    {
        var goal = CreateValidGoal();

        goal.Update("  New Title  ", "  New Desc  ", 200, "  km  ", null);

        goal.Title.Should().Be("New Title");
        goal.Description.Should().Be("New Desc");
        goal.Unit.Should().Be("km");
    }

    [Fact]
    public void Update_NullDescription_SetsNull()
    {
        var goal = CreateValidGoal();

        goal.Update("Title", null, 100, "pages", null);

        goal.Description.Should().BeNull();
    }

    [Fact]
    public void Update_LowerTargetBelowCurrent_AutoCompletes()
    {
        var goal = CreateValidGoal(targetValue: 100);
        goal.UpdateProgress(80);

        goal.Update("Title", null, 50, "pages", null);

        goal.Status.Should().Be(GoalStatus.Completed);
        goal.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Update_RaiseTargetAboveCurrent_ReactivatesCompleted()
    {
        var goal = CreateValidGoal(targetValue: 100);
        goal.UpdateProgress(100); // auto-completes
        goal.Status.Should().Be(GoalStatus.Completed);

        goal.Update("Title", null, 200, "pages", null);

        goal.Status.Should().Be(GoalStatus.Active);
        goal.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Update_ClearDeadline_SetsNull()
    {
        var goal = CreateValidGoal(deadline: new DateOnly(2026, 12, 31));

        goal.Update("Title", null, 100, "pages", null);

        goal.Deadline.Should().BeNull();
    }

    // --- UpdatePosition tests ---

    [Fact]
    public void UpdatePosition_SetsPosition()
    {
        var goal = CreateValidGoal();

        var result = goal.UpdatePosition(3);

        result.IsSuccess.Should().BeTrue();
        goal.Position.Should().Be(3);
    }

    // --- MarkCompleted tests ---

    [Fact]
    public void MarkCompleted_ActiveGoal_Completes()
    {
        var goal = CreateValidGoal();

        var result = goal.MarkCompleted();

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Completed);
        goal.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void MarkCompleted_AlreadyCompleted_ReturnsFailure()
    {
        var goal = CreateValidGoal();
        goal.MarkCompleted();

        var result = goal.MarkCompleted();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Goal is already completed");
    }

    [Fact]
    public void MarkCompleted_AbandonedGoal_Completes()
    {
        var goal = CreateValidGoal();
        goal.MarkAbandoned();

        var result = goal.MarkCompleted();

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Completed);
    }

    // --- MarkAbandoned tests ---

    [Fact]
    public void MarkAbandoned_ActiveGoal_Abandons()
    {
        var goal = CreateValidGoal();

        var result = goal.MarkAbandoned();

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Abandoned);
        goal.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void MarkAbandoned_AlreadyAbandoned_ReturnsFailure()
    {
        var goal = CreateValidGoal();
        goal.MarkAbandoned();

        var result = goal.MarkAbandoned();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Goal is already abandoned");
    }

    [Fact]
    public void MarkAbandoned_CompletedGoal_Abandons()
    {
        var goal = CreateValidGoal();
        goal.MarkCompleted();

        var result = goal.MarkAbandoned();

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Abandoned);
        goal.CompletedAtUtc.Should().BeNull();
    }

    // --- Reactivate tests ---

    [Fact]
    public void Reactivate_CompletedGoal_Reactivates()
    {
        var goal = CreateValidGoal();
        goal.MarkCompleted();

        var result = goal.Reactivate();

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Active);
        goal.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Reactivate_AbandonedGoal_Reactivates()
    {
        var goal = CreateValidGoal();
        goal.MarkAbandoned();

        var result = goal.Reactivate();

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Active);
        goal.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Reactivate_AlreadyActive_ReturnsFailure()
    {
        var goal = CreateValidGoal();

        var result = goal.Reactivate();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Goal is already active");
    }

    // --- State transition sequences ---

    [Fact]
    public void StateTransition_Active_Complete_Reactivate()
    {
        var goal = CreateValidGoal();

        goal.MarkCompleted().IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Completed);

        goal.Reactivate().IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Active);
    }

    [Fact]
    public void StateTransition_Active_Abandon_Reactivate()
    {
        var goal = CreateValidGoal();

        goal.MarkAbandoned().IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Abandoned);

        goal.Reactivate().IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Active);
    }

    [Fact]
    public void StateTransition_Active_Complete_Abandon()
    {
        var goal = CreateValidGoal();

        goal.MarkCompleted().IsSuccess.Should().BeTrue();
        goal.MarkAbandoned().IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Abandoned);
        goal.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void StateTransition_ProgressAutoComplete_ThenReactivate()
    {
        var goal = CreateValidGoal(targetValue: 100);
        goal.UpdateProgress(100);
        goal.Status.Should().Be(GoalStatus.Completed);

        goal.Reactivate();
        goal.Status.Should().Be(GoalStatus.Active);
        goal.CurrentValue.Should().Be(100);
    }

    // --- Habit association tests ---

    [Fact]
    public void AddHabit_AddsHabit()
    {
        var goal = CreateValidGoal();
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1)).Value;

        goal.AddHabit(habit);

        goal.Habits.Should().ContainSingle().Which.Should().Be(habit);
    }

    [Fact]
    public void AddHabit_DuplicateHabit_NoOp()
    {
        var goal = CreateValidGoal();
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1)).Value;

        goal.AddHabit(habit);
        goal.AddHabit(habit);

        goal.Habits.Should().HaveCount(1);
    }

    [Fact]
    public void RemoveHabit_ExistingHabit_Removes()
    {
        var goal = CreateValidGoal();
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1)).Value;
        goal.AddHabit(habit);

        goal.RemoveHabit(habit);

        goal.Habits.Should().BeEmpty();
    }

    [Fact]
    public void RemoveHabit_NonExistentHabit_NoOp()
    {
        var goal = CreateValidGoal();
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1)).Value;

        goal.RemoveHabit(habit);

        goal.Habits.Should().BeEmpty();
    }
}
