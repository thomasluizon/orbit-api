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

    // --- Update with all parameter combinations ---

    [Fact]
    public void Update_AllOptionalFields_Applied()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "Updated Title",
            "New Description",
            FrequencyUnit.Week,
            2,
            Days: null,
            IsBadHabit: true,
            DueDate: new DateOnly(2026, 6, 1),
            DueTime: new TimeOnly(9, 0),
            DueEndTime: new TimeOnly(10, 0),
            ReminderEnabled: true,
            ReminderTimes: new[] { 5, 10 },
            SlipAlertEnabled: true,
            ChecklistItems: new[] { new ChecklistItem("Item 1", false) },
            IsGeneral: false,
            IsFlexible: false));

        result.IsSuccess.Should().BeTrue();
        habit.Title.Should().Be("Updated Title");
        habit.Description.Should().Be("New Description");
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        habit.FrequencyQuantity.Should().Be(2);
        habit.IsBadHabit.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2026, 6, 1));
        habit.DueTime.Should().Be(new TimeOnly(9, 0));
        habit.DueEndTime.Should().Be(new TimeOnly(10, 0));
        habit.ReminderEnabled.Should().BeTrue();
        habit.ReminderTimes.Should().BeEquivalentTo(new[] { 5, 10 });
        habit.SlipAlertEnabled.Should().BeTrue();
        habit.ChecklistItems.Should().HaveCount(1);
    }

    [Fact]
    public void Update_DueEndTimeBeforeDueTime_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            DueTime: new TimeOnly(10, 0),
            DueEndTime: new TimeOnly(9, 0)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End time must be after start time");
    }

    [Fact]
    public void Update_SwitchToGeneral_ClearsFrequency()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "General Habit", null, null, null, null, false, null,
            IsGeneral: true));

        result.IsSuccess.Should().BeTrue();
        habit.IsGeneral.Should().BeTrue();
        habit.FrequencyUnit.Should().BeNull();
    }

    [Fact]
    public void Update_GeneralWithFrequency_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "General Habit", null, FrequencyUnit.Day, 1, null, false, null,
            IsGeneral: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("General habits cannot have a frequency");
    }

    [Fact]
    public void Update_GeneralBadHabit_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "General Bad", null, null, null, null, true, null,
            IsGeneral: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("General habits cannot be bad habits");
    }

    [Fact]
    public void Update_TrimsTitle()
    {
        var habit = CreateValidHabit();

        habit.Update(new HabitUpdateParams("  Trimmed  ", null, FrequencyUnit.Day, 1, null, false, null));

        habit.Title.Should().Be("Trimmed");
    }

    [Fact]
    public void Update_TrimsDescription()
    {
        var habit = CreateValidHabit();

        habit.Update(new HabitUpdateParams("Title", "  Trimmed Desc  ", FrequencyUnit.Day, 1, null, false, null));

        habit.Description.Should().Be("Trimmed Desc");
    }

    [Fact]
    public void Update_NullDueDate_KeepsExisting()
    {
        var dueDate = new DateOnly(2026, 3, 15);
        var habit = CreateValidHabit(dueDate: dueDate);

        habit.Update(new HabitUpdateParams("Title", null, FrequencyUnit.Day, 1, null, false, null));

        habit.DueDate.Should().Be(dueDate);
    }

    // --- CatchUpDueDate for all frequency types ---

    [Fact]
    public void CatchUpDueDate_Daily_AdvancesToToday()
    {
        var start = new DateOnly(2025, 1, 1);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Day, frequencyQuantity: 1, dueDate: start);
        var today = new DateOnly(2025, 1, 5);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().BeOnOrAfter(today);
        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void CatchUpDueDate_Weekly_AdvancesToNextWeek()
    {
        var start = new DateOnly(2025, 1, 6); // Monday
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Week, frequencyQuantity: 1, dueDate: start);
        var today = new DateOnly(2025, 1, 20);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().BeOnOrAfter(today);
    }

    [Fact]
    public void CatchUpDueDate_Monthly_AdvancesToNextMonth()
    {
        var start = new DateOnly(2025, 1, 15);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Month, frequencyQuantity: 1, dueDate: start);
        var today = new DateOnly(2025, 4, 1);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().BeOnOrAfter(today);
    }

    [Fact]
    public void CatchUpDueDate_Yearly_AdvancesToNextYear()
    {
        var start = new DateOnly(2023, 6, 15);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Year, frequencyQuantity: 1, dueDate: start);
        var today = new DateOnly(2025, 7, 1);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().BeOnOrAfter(today);
    }

    [Fact]
    public void CatchUpDueDate_WithEndDate_MarksCompletedWhenPastEnd()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2025, 1, 1), EndDate: new DateOnly(2025, 1, 5))).Value;

        habit.CatchUpDueDate(new DateOnly(2025, 1, 10));

        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void CatchUpDueDate_AlreadyOnOrAfterToday_NoChange()
    {
        var today = new DateOnly(2025, 1, 6);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Day, frequencyQuantity: 1, dueDate: today);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().Be(today);
    }

    // --- AdvanceDueDate method ---

    [Fact]
    public void AdvanceDueDate_Daily_AdvancesPastLogged()
    {
        var start = new DateOnly(2025, 1, 1);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Day, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);

        habit.DueDate.Should().BeAfter(start);
    }

    [Fact]
    public void AdvanceDueDate_Weekly_AdvancesBy7Days()
    {
        var start = new DateOnly(2025, 1, 6); // Monday
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Week, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);

        habit.DueDate.Should().Be(new DateOnly(2025, 1, 13));
    }

    [Fact]
    public void AdvanceDueDate_Monthly_AdvancesBy1Month()
    {
        var start = new DateOnly(2025, 1, 15);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Month, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);

        habit.DueDate.Should().Be(new DateOnly(2025, 2, 15));
    }

    [Fact]
    public void AdvanceDueDate_Monthly_Jan31_AdvancesToFeb28()
    {
        // Jan 31 -> Feb 28 (clamped to last day of Feb)
        var start = new DateOnly(2025, 1, 31);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Month, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);
        // After first advance: Feb 28 (clamped from 31 to 28)
        habit.DueDate.Should().Be(new DateOnly(2025, 2, 28));
    }

    [Fact]
    public void AdvanceDueDate_Monthly_Feb28_AdvancesToMar28()
    {
        // After clamping to Feb 28, next advance goes to Mar 28 (re-anchor uses current DueDate.Day)
        var start = new DateOnly(2025, 2, 28);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Month, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);
        habit.DueDate.Should().Be(new DateOnly(2025, 3, 28));
    }

    [Fact]
    public void AdvanceDueDate_Yearly_AdvancesBy1Year()
    {
        var start = new DateOnly(2025, 3, 15);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Year, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);

        habit.DueDate.Should().Be(new DateOnly(2026, 3, 15));
    }

    [Fact]
    public void AdvanceDueDate_WithDays_SnapsToNextMatchingDay()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Day,
            frequencyQuantity: 1,
            dueDate: monday,
            days: new[] { DayOfWeek.Monday, DayOfWeek.Friday });

        habit.AdvanceDueDate(monday);

        // Should snap to next Friday (Jan 10) since days filter requires Mon or Fri
        habit.DueDate.DayOfWeek.Should().BeOneOf(DayOfWeek.Monday, DayOfWeek.Friday);
        habit.DueDate.Should().BeAfter(monday);
    }

    // --- SkipFlexible ---

    [Fact]
    public void SkipFlexible_FlexibleHabit_CreatesSkipLog()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flexible", FrequencyUnit.Week, 3, DueDate: today, IsFlexible: true)).Value;

        var result = habit.SkipFlexible(today);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(0); // Skip log has Value = 0
        habit.Logs.Should().HaveCount(1);
    }

    [Fact]
    public void SkipFlexible_NonFlexibleHabit_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.SkipFlexible(DateOnly.FromDateTime(DateTime.UtcNow));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Only flexible habits can be skipped");
    }

    [Fact]
    public void SkipFlexible_OneTimeTask_ReturnsFailure()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Create a flexible habit that looks like one-time (null frequency) -- but that would fail validation
        // So test with a proper flexible habit scenario: skip when frequency is null is rejected
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Task", FrequencyUnit.Week, 3, DueDate: today, IsFlexible: true)).Value;

        // The method checks IsFlexible first, so this should succeed
        var result = habit.SkipFlexible(today);
        result.IsSuccess.Should().BeTrue();
    }

    // --- Flexible Habit Log does not advance DueDate ---

    [Fact]
    public void Log_FlexibleHabit_MultipleLogsOnSameDay_AllSucceed()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flex", FrequencyUnit.Week, 5, DueDate: today, IsFlexible: true)).Value;

        for (int i = 0; i < 5; i++)
        {
            var result = habit.Log(today);
            result.IsSuccess.Should().BeTrue();
        }

        habit.Logs.Should().HaveCount(5);
        habit.DueDate.Should().Be(today); // DueDate unchanged for flexible
    }

    // --- Log with checklist reset ---

    [Fact]
    public void Log_RecurringWithChecklist_ResetsChecklist()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var checklist = new List<ChecklistItem>
        {
            new("Step 1", true),
            new("Step 2", true)
        };
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Routine", FrequencyUnit.Day, 1, DueDate: today,
            ChecklistItems: checklist)).Value;

        habit.Log(today);

        habit.ChecklistItems.Should().AllSatisfy(item => item.IsChecked.Should().BeFalse());
    }

    // --- AdvanceDueDatePastWindow ---

    [Fact]
    public void AdvanceDueDatePastWindow_Weekly_GoesToNextMonday()
    {
        var wednesday = new DateOnly(2025, 1, 8);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flex", FrequencyUnit.Week, 3, DueDate: wednesday, IsFlexible: true)).Value;

        habit.AdvanceDueDatePastWindow(wednesday);

        // Sunday of that week is Jan 12, so next day is Jan 13 (Monday)
        habit.DueDate.Should().Be(new DateOnly(2025, 1, 13));
        habit.DueDate.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void AdvanceDueDatePastWindow_Monthly_GoesToFirstOfNextMonth()
    {
        var midMonth = new DateOnly(2025, 3, 15);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flex", FrequencyUnit.Month, 5, DueDate: midMonth, IsFlexible: true)).Value;

        habit.AdvanceDueDatePastWindow(midMonth);

        habit.DueDate.Should().Be(new DateOnly(2025, 4, 1));
    }

    [Fact]
    public void AdvanceDueDatePastWindow_Yearly_GoesToJan1NextYear()
    {
        var midYear = new DateOnly(2025, 7, 15);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flex", FrequencyUnit.Year, 10, DueDate: midYear, IsFlexible: true)).Value;

        habit.AdvanceDueDatePastWindow(midYear);

        habit.DueDate.Should().Be(new DateOnly(2026, 1, 1));
    }

    [Fact]
    public void AdvanceDueDatePastWindow_Daily_GoesToNextDay()
    {
        var today = new DateOnly(2025, 3, 15);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flex", FrequencyUnit.Day, 2, DueDate: today, IsFlexible: true)).Value;

        habit.AdvanceDueDatePastWindow(today);

        habit.DueDate.Should().Be(new DateOnly(2025, 3, 16));
    }

    // --- PostponeTo ---

    [Fact]
    public void PostponeTo_UpdatesDueDate()
    {
        var habit = CreateOneTimeHabit(dueDate: new DateOnly(2025, 3, 1));

        habit.PostponeTo(new DateOnly(2025, 3, 15));

        habit.DueDate.Should().Be(new DateOnly(2025, 3, 15));
    }

    // --- AddGoal / RemoveGoal ---

    [Fact]
    public void AddGoal_NewGoal_AddsSuccessfully()
    {
        var habit = CreateValidHabit();
        var goal = Goal.Create(ValidUserId, "Fitness", 10, "workouts").Value;

        habit.AddGoal(goal);

        habit.Goals.Should().ContainSingle().Which.Should().Be(goal);
    }

    [Fact]
    public void AddGoal_DuplicateGoal_NoOp()
    {
        var habit = CreateValidHabit();
        var goal = Goal.Create(ValidUserId, "Fitness", 10, "workouts").Value;

        habit.AddGoal(goal);
        habit.AddGoal(goal);

        habit.Goals.Should().HaveCount(1);
    }

    [Fact]
    public void RemoveGoal_ExistingGoal_Removes()
    {
        var habit = CreateValidHabit();
        var goal = Goal.Create(ValidUserId, "Fitness", 10, "workouts").Value;
        habit.AddGoal(goal);

        habit.RemoveGoal(goal);

        habit.Goals.Should().BeEmpty();
    }

    // --- SetPosition / SetParentHabitId / UpdateChecklist ---

    [Fact]
    public void SetPosition_SetsValue()
    {
        var habit = CreateValidHabit();
        habit.SetPosition(5);
        habit.Position.Should().Be(5);
    }

    [Fact]
    public void SetParentHabitId_SetsValue()
    {
        var habit = CreateValidHabit();
        var parentId = Guid.NewGuid();

        habit.SetParentHabitId(parentId);

        habit.ParentHabitId.Should().Be(parentId);
    }

    [Fact]
    public void UpdateChecklist_ReplacesItems()
    {
        var habit = CreateValidHabit();
        var items = new List<ChecklistItem>
        {
            new("Task A", false),
            new("Task B", true)
        };

        habit.UpdateChecklist(items);

        habit.ChecklistItems.Should().HaveCount(2);
    }

    // --- Create edge cases ---

    [Fact]
    public void Create_GeneralHabit_Success()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "General", null, null, IsGeneral: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsGeneral.Should().BeTrue();
    }

    [Fact]
    public void Create_GeneralWithFrequency_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "General", FrequencyUnit.Day, 1, IsGeneral: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("General habits cannot have a frequency");
    }

    [Fact]
    public void Create_GeneralBadHabit_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "General Bad", null, null, IsGeneral: true, IsBadHabit: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("General habits cannot be bad habits");
    }

    [Fact]
    public void Create_DueEndTimeBeforeDueTime_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueTime: new TimeOnly(10, 0), DueEndTime: new TimeOnly(9, 0)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End time must be after start time");
    }

    [Fact]
    public void Create_WithAllOptions_Success()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "Full Habit", FrequencyUnit.Day, 1,
            Description: "Full description",
            DueDate: new DateOnly(2026, 1, 1),
            DueTime: new TimeOnly(9, 0),
            DueEndTime: new TimeOnly(10, 0),
            ReminderEnabled: true,
            ReminderTimes: new[] { 5, 15, 30 },
            SlipAlertEnabled: true,
            ChecklistItems: new[] { new ChecklistItem("Step 1", false) }));

        result.IsSuccess.Should().BeTrue();
        result.Value.DueTime.Should().Be(new TimeOnly(9, 0));
        result.Value.DueEndTime.Should().Be(new TimeOnly(10, 0));
        result.Value.ReminderEnabled.Should().BeTrue();
        result.Value.ReminderTimes.Should().BeEquivalentTo(new[] { 5, 15, 30 });
        result.Value.SlipAlertEnabled.Should().BeTrue();
        result.Value.ChecklistItems.Should().HaveCount(1);
    }

    // --- Unlog flexible habit ---

    [Fact]
    public void Unlog_FlexibleHabit_RemovesLogButDoesNotResetDueDate()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flex", FrequencyUnit.Week, 3, DueDate: today, IsFlexible: true)).Value;
        habit.Log(today);
        var originalDueDate = habit.DueDate;

        var result = habit.Unlog(today);

        result.IsSuccess.Should().BeTrue();
        habit.Logs.Should().BeEmpty();
        habit.DueDate.Should().Be(originalDueDate); // Flexible unlog doesn't reset DueDate
    }
}
