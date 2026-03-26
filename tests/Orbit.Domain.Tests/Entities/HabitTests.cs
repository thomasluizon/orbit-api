using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    private static Habit CreateValidHabit(
        FrequencyUnit? frequencyUnit = FrequencyUnit.Day,
        int? frequencyQuantity = 1,
        bool isBadHabit = false,
        DateOnly? dueDate = null,
        IReadOnlyList<DayOfWeek>? days = null,
        Guid? parentHabitId = null)
    {
        var result = Habit.Create(
            ValidUserId,
            "Exercise",
            frequencyUnit,
            frequencyQuantity,
            description: "Daily workout",
            days: days,
            isBadHabit: isBadHabit,
            dueDate: dueDate,
            parentHabitId: parentHabitId);
        return result.Value;
    }

    private static Habit CreateOneTimeHabit(DateOnly? dueDate = null)
    {
        return Habit.Create(
            ValidUserId,
            "One-time task",
            frequencyUnit: null,
            frequencyQuantity: null,
            dueDate: dueDate).Value;
    }

    // --- Create tests ---

    [Fact]
    public void Create_ValidInput_ReturnsSuccess()
    {
        var result = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, 1);

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Exercise");
        result.Value.UserId.Should().Be(ValidUserId);
        result.Value.IsActive.Should().BeTrue();
        result.Value.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Create_EmptyUserId_ReturnsFailure()
    {
        var result = Habit.Create(Guid.Empty, "Exercise", FrequencyUnit.Day, 1);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User ID is required");
    }

    [Fact]
    public void Create_EmptyTitle_ReturnsFailure()
    {
        var result = Habit.Create(ValidUserId, "", FrequencyUnit.Day, 1);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Create_NegativeFrequencyQty_ReturnsFailure()
    {
        var result = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, -1);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Frequency quantity must be greater than 0");
    }

    [Fact]
    public void Create_ZeroFrequencyQty_ReturnsFailure()
    {
        var result = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Frequency quantity must be greater than 0");
    }

    [Fact]
    public void Create_DaysWithQtyGreaterThan1_ReturnsFailure()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };

        var result = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Week, 2, days: days);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Days can only be set when frequency quantity is 1");
    }

    [Fact]
    public void Create_NullDueDate_DefaultsToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var habit = CreateValidHabit(dueDate: null);

        habit.DueDate.Should().Be(today);
    }

    [Fact]
    public void Create_TrimsTitle()
    {
        var result = Habit.Create(ValidUserId, "  Exercise  ", FrequencyUnit.Day, 1);

        result.Value.Title.Should().Be("Exercise");
    }

    [Fact]
    public void Create_SetsParentHabitId()
    {
        var parentId = Guid.NewGuid();

        var habit = CreateValidHabit(parentHabitId: parentId);

        habit.ParentHabitId.Should().Be(parentId);
    }

    // --- Log tests ---

    [Fact]
    public void Log_ActiveHabit_CreatesLog()
    {
        var habit = CreateValidHabit(dueDate: DateOnly.FromDateTime(DateTime.UtcNow));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var result = habit.Log(today, "Done");

        result.IsSuccess.Should().BeTrue();
        result.Value.Date.Should().Be(today);
        habit.Logs.Should().HaveCount(1);
    }

    [Fact]
    public void Log_InactiveHabit_ReturnsFailure()
    {
        var habit = CreateValidHabit();
        habit.Deactivate();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var result = habit.Log(today);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Cannot log an inactive habit");
    }

    [Fact]
    public void Log_CompletedHabit_ReturnsFailure()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateOneTimeHabit(dueDate: today);
        habit.Log(today); // completes one-time

        var result = habit.Log(today);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Cannot log a completed habit");
    }

    [Fact]
    public void Log_AlreadyLoggedDate_ReturnsFailure()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(dueDate: today);
        habit.Log(today);

        var result = habit.Log(today);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already been logged for this date");
    }

    [Fact]
    public void Log_BadHabit_AllowsDuplicateDate()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(isBadHabit: true, dueDate: today);
        habit.Log(today);

        var result = habit.Log(today);

        result.IsSuccess.Should().BeTrue();
        habit.Logs.Should().HaveCount(2);
    }

    [Fact]
    public void Log_OneTime_MarksCompleted()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateOneTimeHabit(dueDate: today);

        habit.Log(today);

        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void Log_Recurring_AdvancesDueDate()
    {
        var startDate = new DateOnly(2025, 1, 1);
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Day,
            frequencyQuantity: 1,
            dueDate: startDate);

        habit.Log(startDate);

        habit.DueDate.Should().BeAfter(startDate);
    }

    [Fact]
    public void Log_RecurringWithDays_AdvancesToNextMatchingDay()
    {
        // Monday Jan 6, 2025
        var monday = new DateOnly(2025, 1, 6);
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Week,
            frequencyQuantity: 1,
            dueDate: monday,
            days: days);

        habit.Log(monday);

        // Should advance to the next matching day (Wednesday Jan 8)
        habit.DueDate.DayOfWeek.Should().BeOneOf(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday);
        habit.DueDate.Should().BeAfter(monday);
    }

    // --- Unlog tests ---

    [Fact]
    public void Unlog_ExistingLog_RemovesAndReturns()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(dueDate: today);
        habit.Log(today);

        var result = habit.Unlog(today);

        result.IsSuccess.Should().BeTrue();
        habit.Logs.Should().BeEmpty();
    }

    [Fact]
    public void Unlog_NoLogForDate_ReturnsFailure()
    {
        var habit = CreateValidHabit();
        var date = new DateOnly(2025, 6, 15);

        var result = habit.Unlog(date);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No log found for this date");
    }

    [Fact]
    public void Unlog_InactiveHabit_ReturnsFailure()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(dueDate: today);
        habit.Log(today);
        habit.Deactivate();

        var result = habit.Unlog(today);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Cannot unlog an inactive habit");
    }

    [Fact]
    public void Unlog_OneTime_RevertsCompleted()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateOneTimeHabit(dueDate: today);
        habit.Log(today);
        habit.IsCompleted.Should().BeTrue();

        habit.Unlog(today);

        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Unlog_Recurring_ResetsDueDate()
    {
        var startDate = new DateOnly(2025, 1, 1);
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Day,
            frequencyQuantity: 1,
            dueDate: startDate);
        habit.Log(startDate);
        habit.DueDate.Should().NotBe(startDate);

        habit.Unlog(startDate);

        habit.DueDate.Should().Be(startDate);
    }

    // --- Update tests ---

    [Fact]
    public void Update_ValidInput_UpdatesFields()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(
            "Running",
            "Morning jog",
            FrequencyUnit.Week,
            2,
            days: null,
            isBadHabit: false,
            dueDate: new DateOnly(2025, 6, 1));

        result.IsSuccess.Should().BeTrue();
        habit.Title.Should().Be("Running");
        habit.Description.Should().Be("Morning jog");
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        habit.FrequencyQuantity.Should().Be(2);
        habit.DueDate.Should().Be(new DateOnly(2025, 6, 1));
    }

    [Fact]
    public void Update_EmptyTitle_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update("", null, FrequencyUnit.Day, 1, null, false, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Update_NegativeFrequencyQty_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update("Exercise", null, FrequencyUnit.Day, -5, null, false, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Frequency quantity must be greater than 0");
    }

    // --- Tag tests ---

    [Fact]
    public void AddTag_NewTag_Adds()
    {
        var habit = CreateValidHabit();
        var tag = Tag.Create(ValidUserId, "Fitness", "#FF0000").Value;

        habit.AddTag(tag);

        habit.Tags.Should().ContainSingle().Which.Should().Be(tag);
    }

    [Fact]
    public void AddTag_DuplicateTag_NoOp()
    {
        var habit = CreateValidHabit();
        var tag = Tag.Create(ValidUserId, "Fitness", "#FF0000").Value;

        habit.AddTag(tag);
        habit.AddTag(tag);

        habit.Tags.Should().HaveCount(1);
    }

    [Fact]
    public void RemoveTag_ExistingTag_Removes()
    {
        var habit = CreateValidHabit();
        var tag = Tag.Create(ValidUserId, "Fitness", "#FF0000").Value;
        habit.AddTag(tag);

        habit.RemoveTag(tag);

        habit.Tags.Should().BeEmpty();
    }

    // --- EndDate ---

    [Fact]
    public void Create_EndDateOnOneTime_ReturnsFailure()
    {
        var result = Habit.Create(ValidUserId, "Task", frequencyUnit: null, frequencyQuantity: null,
            dueDate: new DateOnly(2026, 1, 1), endDate: new DateOnly(2026, 6, 30));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("One-time tasks cannot have an end date");
    }

    [Fact]
    public void Create_EndDateBeforeDueDate_ReturnsFailure()
    {
        var result = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            dueDate: new DateOnly(2026, 6, 1), endDate: new DateOnly(2026, 1, 1));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End date must be on or after the start date");
    }

    [Fact]
    public void Create_ValidEndDate_Success()
    {
        var result = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            dueDate: new DateOnly(2026, 1, 1), endDate: new DateOnly(2026, 6, 30));
        result.IsSuccess.Should().BeTrue();
        result.Value.EndDate.Should().Be(new DateOnly(2026, 6, 30));
    }

    [Fact]
    public void AdvanceDueDate_PastEndDate_MarksCompleted()
    {
        var habit = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            dueDate: new DateOnly(2026, 1, 29), endDate: new DateOnly(2026, 1, 31)).Value;
        habit.AdvanceDueDate(new DateOnly(2026, 1, 31));
        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void AdvanceDueDate_NotPastEndDate_StaysActive()
    {
        var habit = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            dueDate: new DateOnly(2026, 1, 1), endDate: new DateOnly(2026, 12, 31)).Value;
        habit.AdvanceDueDate(new DateOnly(2026, 1, 1));
        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Update_ClearEndDate_SetsNull()
    {
        var habit = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            dueDate: new DateOnly(2026, 1, 1), endDate: new DateOnly(2026, 6, 30)).Value;
        habit.Update("Exercise", null, FrequencyUnit.Day, 1, null, false, null, clearEndDate: true);
        habit.EndDate.Should().BeNull();
    }

    [Fact]
    public void Update_SetEndDate()
    {
        var habit = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            dueDate: new DateOnly(2026, 1, 1)).Value;
        habit.Update("Exercise", null, FrequencyUnit.Day, 1, null, false, null, endDate: new DateOnly(2026, 12, 31));
        habit.EndDate.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void Update_EndDateBeforeDueDate_ReturnsFailure()
    {
        var habit = Habit.Create(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            dueDate: new DateOnly(2026, 6, 1)).Value;
        var result = habit.Update("Exercise", null, FrequencyUnit.Day, 1, null, false, null, endDate: new DateOnly(2026, 1, 1));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End date must be on or after the start date");
    }

    // --- Deactivate ---

    [Fact]
    public void Deactivate_SetsIsActiveFalse()
    {
        var habit = CreateValidHabit();

        habit.Deactivate();

        habit.IsActive.Should().BeFalse();
    }
}
