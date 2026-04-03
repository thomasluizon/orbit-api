using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

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
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            frequencyUnit,
            frequencyQuantity,
            Description: "Daily workout",
            Days: days,
            IsBadHabit: isBadHabit,
            DueDate: dueDate,
            ParentHabitId: parentHabitId));
        return result.Value;
    }

    private static Habit CreateOneTimeHabit(DateOnly? dueDate = null)
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            "One-time task",
            FrequencyUnit: null,
            FrequencyQuantity: null,
            DueDate: dueDate)).Value;
    }

    // --- Create tests ---

    [Fact]
    public void Create_ValidInput_ReturnsSuccess()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1));

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Exercise");
        result.Value.UserId.Should().Be(ValidUserId);
        result.Value.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Create_EmptyUserId_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(Guid.Empty, "Exercise", FrequencyUnit.Day, 1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User ID is required");
    }

    [Fact]
    public void Create_EmptyTitle_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "", FrequencyUnit.Day, 1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Create_NegativeFrequencyQty_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, -1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Frequency quantity must be greater than 0");
    }

    [Fact]
    public void Create_ZeroFrequencyQty_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 0));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Frequency quantity must be greater than 0");
    }

    [Fact]
    public void Create_DaysWithQtyGreaterThan1_ReturnsFailure()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Week, 2, Days: days));

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
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "  Exercise  ", FrequencyUnit.Day, 1));

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

        var result = habit.Update(new HabitUpdateParams(
            "Running",
            "Morning jog",
            FrequencyUnit.Week,
            2,
            Days: null,
            IsBadHabit: false,
            DueDate: new DateOnly(2025, 6, 1)));

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

        var result = habit.Update(new HabitUpdateParams("", null, FrequencyUnit.Day, 1, null, false, null));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Update_NegativeFrequencyQty_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, -5, null, false, null));

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
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Task", FrequencyUnit: null, FrequencyQuantity: null,
            DueDate: new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 6, 30)));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("One-time tasks cannot have an end date");
    }

    [Fact]
    public void Create_EndDateBeforeDueDate_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 6, 1), EndDate: new DateOnly(2026, 1, 1)));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End date must be on or after the start date");
    }

    [Fact]
    public void Create_ValidEndDate_Success()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 6, 30)));
        result.IsSuccess.Should().BeTrue();
        result.Value.EndDate.Should().Be(new DateOnly(2026, 6, 30));
    }

    [Fact]
    public void AdvanceDueDate_PastEndDate_MarksCompleted()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 29), EndDate: new DateOnly(2026, 1, 31))).Value;
        habit.AdvanceDueDate(new DateOnly(2026, 1, 31));
        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void AdvanceDueDate_NotPastEndDate_StaysActive()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 12, 31))).Value;
        habit.AdvanceDueDate(new DateOnly(2026, 1, 1));
        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Update_ClearEndDate_SetsNull()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 6, 30))).Value;
        habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null, ClearEndDate: true));
        habit.EndDate.Should().BeNull();
    }

    [Fact]
    public void Update_SetEndDate()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 1))).Value;
        habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null, EndDate: new DateOnly(2026, 12, 31)));
        habit.EndDate.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void Update_EndDateBeforeDueDate_ReturnsFailure()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 6, 1))).Value;
        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null, EndDate: new DateOnly(2026, 1, 1)));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End date must be on or after the start date");
    }

    // --- Flexible Habit Create tests ---

    [Fact]
    public void Create_Flexible_WithFrequency_ReturnsSuccess()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            IsFlexible: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFlexible.Should().BeTrue();
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        result.Value.FrequencyQuantity.Should().Be(3);
    }

    [Fact]
    public void Create_Flexible_WithoutFrequencyUnit_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit: null,
            FrequencyQuantity: null,
            IsFlexible: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Flexible habits must have a frequency unit");
    }

    [Fact]
    public void Create_Flexible_WithDays_ReturnsFailure()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };

        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            Days: days,
            IsFlexible: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Flexible habits cannot have specific days set");
    }

    [Fact]
    public void Create_Flexible_DefaultsFalse()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFlexible.Should().BeFalse();
    }

    // --- Flexible Habit Log tests ---

    [Fact]
    public void Log_FlexibleHabit_AllowsMultipleLogsPerDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            DueDate: today,
            IsFlexible: true));
        var habit = result.Value;

        var log1 = habit.Log(today);
        var log2 = habit.Log(today);

        log1.IsSuccess.Should().BeTrue();
        log2.IsSuccess.Should().BeTrue();
        habit.Logs.Should().HaveCount(2);
    }

    [Fact]
    public void Log_FlexibleHabit_DoesNotAdvanceDueDate()
    {
        var startDate = new DateOnly(2025, 1, 6);
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            DueDate: startDate,
            IsFlexible: true));
        var habit = result.Value;

        habit.Log(startDate);

        habit.DueDate.Should().Be(startDate);
    }

    [Fact]
    public void Log_NonFlexibleHabit_StillBlocksDuplicateDate()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(dueDate: today);
        habit.Log(today);

        var result = habit.Log(today);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already been logged for this date");
    }

    // --- Flexible Habit Update tests ---

    [Fact]
    public void Update_SwitchToFlexible_ClearsDays()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Day,
            frequencyQuantity: 1,
            days: days);
        habit.Days.Should().HaveCount(2);

        var result = habit.Update(new HabitUpdateParams(
            "Exercise",
            null,
            FrequencyUnit.Week,
            3,
            Days: null,
            IsBadHabit: false,
            DueDate: null,
            IsFlexible: true));

        result.IsSuccess.Should().BeTrue();
        habit.IsFlexible.Should().BeTrue();
        habit.Days.Should().BeEmpty();
    }

    [Fact]
    public void Update_FlexibleWithDays_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            IsFlexible: true));
        var habit = result.Value;

        var updateResult = habit.Update(new HabitUpdateParams(
            "Exercise",
            null,
            FrequencyUnit.Week,
            3,
            Days: new[] { DayOfWeek.Monday },
            IsBadHabit: false,
            DueDate: null,
            IsFlexible: true));

        updateResult.IsFailure.Should().BeTrue();
        updateResult.Error.Should().Contain("Flexible habits cannot have specific days set");
    }

    [Fact]
    public void Update_FlexibleWithoutFrequency_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            IsFlexible: true));
        var habit = result.Value;

        var updateResult = habit.Update(new HabitUpdateParams(
            "Exercise",
            null,
            FrequencyUnit: null,
            FrequencyQuantity: null,
            Days: null,
            IsBadHabit: false,
            DueDate: null,
            IsFlexible: true));

        updateResult.IsFailure.Should().BeTrue();
        updateResult.Error.Should().Contain("Flexible habits must have a frequency unit");
    }

    // --- Scheduled Reminders ---

    [Fact]
    public void Create_WithValidScheduledReminders_ReturnsSuccess()
    {
        var reminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0)),
            new(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0))
        };

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            ScheduledReminders: reminders));

        result.IsSuccess.Should().BeTrue();
        result.Value.ScheduledReminders.Should().HaveCount(2);
    }

    [Fact]
    public void Create_WithEmptyScheduledReminders_DefaultsToEmpty()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1));

        result.IsSuccess.Should().BeTrue();
        result.Value.ScheduledReminders.Should().BeEmpty();
    }

    [Fact]
    public void Create_ScheduledReminders_OverLimit_ReturnsFailure()
    {
        var reminders = Enumerable.Range(0, 6)
            .Select(i => new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8 + i, 0)))
            .ToList();

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            ScheduledReminders: reminders));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("at most 5 scheduled reminders");
    }

    [Fact]
    public void Create_ScheduledReminders_Duplicates_ReturnsFailure()
    {
        var reminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0)),
            new(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0))
        };

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            ScheduledReminders: reminders));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("duplicate entries");
    }

    [Fact]
    public void Update_WithScheduledReminders_SetsReminders()
    {
        var habit = CreateValidHabit();
        var reminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0))
        };

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            ScheduledReminders: reminders));

        result.IsSuccess.Should().BeTrue();
        habit.ScheduledReminders.Should().HaveCount(1);
        habit.ScheduledReminders[0].When.Should().Be(ScheduledReminderWhen.DayBefore);
    }

    [Fact]
    public void Update_ScheduledReminders_OverLimit_ReturnsFailure()
    {
        var habit = CreateValidHabit();
        var reminders = Enumerable.Range(0, 6)
            .Select(i => new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8 + i, 0)))
            .ToList();

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            ScheduledReminders: reminders));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("at most 5 scheduled reminders");
    }

    [Fact]
    public void Update_ScheduledReminders_Null_KeepsExisting()
    {
        var reminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0))
        };
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            ScheduledReminders: reminders)).Value;

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            ScheduledReminders: null));

        result.IsSuccess.Should().BeTrue();
        habit.ScheduledReminders.Should().HaveCount(1);
    }
}
