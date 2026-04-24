using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class UpdateHabitToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly UpdateHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public UpdateHabitToolTests()
    {
        _tool = new UpdateHabitTool(_habitRepo);
    }

    [Fact]
    public async Task SuccessfulUpdate_ReturnsSuccess()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "title": "Drink Water"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Drink Water");
    }

    [Fact]
    public async Task HabitNotFound_ReturnsError()
    {
        var id = Guid.NewGuid();
        SetupHabitNotFound();

        var result = await Execute($$$"""{"habit_id": "{{{id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task UpdateTitleOnly_KeepsOtherProperties()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "title": "Drink Water"}""");

        result.Success.Should().BeTrue();
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Day);
    }

    [Fact]
    public async Task UpdateFrequency_ChangesFrequency()
    {
        var habit = CreateHabit("Exercise", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "frequency_unit": "Week", "frequency_quantity": 3}""");

        result.Success.Should().BeTrue();
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        habit.FrequencyQuantity.Should().Be(3);
    }

    [Fact]
    public async Task PartialUpdate_OnlyChangesProvidedFields()
    {
        var habit = CreateHabit("Read", FrequencyUnit.Week, 2);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "is_bad_habit": true}""");

        result.Success.Should().BeTrue();
        habit.Title.Should().Be("Read");
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        habit.IsBadHabit.Should().BeTrue();
    }

    [Fact]
    public async Task ClearFrequency_ConvertsToOneTime()
    {
        var habit = CreateHabit("Task", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "frequency_unit": null}""");

        result.Success.Should().BeTrue();
        habit.FrequencyUnit.Should().BeNull();
    }

    [Fact]
    public async Task InvalidGuid_ReturnsError()
    {
        var result = await Execute("""{"habit_id": "not-a-guid"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    // ── Update days ──

    [Fact]
    public async Task UpdateDays_ChangesDaySchedule()
    {
        var habit = CreateHabit("Gym", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "days": ["Monday", "Wednesday", "Friday"]
        }
        """);

        result.Success.Should().BeTrue();
        habit.Days.Should().Contain(DayOfWeek.Monday);
        habit.Days.Should().Contain(DayOfWeek.Wednesday);
        habit.Days.Should().Contain(DayOfWeek.Friday);
    }

    // ── Update due time ──

    [Fact]
    public async Task UpdateDueTime_ChangesTime()
    {
        var habit = CreateHabit("Meditate", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "due_time": "07:30"}""");

        result.Success.Should().BeTrue();
        habit.DueTime.Should().Be(new TimeOnly(7, 30));
    }

    [Fact]
    public async Task ClearDueTime_SetsToNull()
    {
        var habit = CreateHabitWithTime("Meditate", FrequencyUnit.Day, 1, new TimeOnly(8, 0));
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "due_time": null}""");

        result.Success.Should().BeTrue();
        habit.DueTime.Should().BeNull();
    }

    // ── Update checklist ──

    [Fact]
    public async Task UpdateChecklist_ReplacesChecklistItems()
    {
        var habit = CreateHabit("Morning", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "checklist_items": [
                {"text": "New item 1"},
                {"text": "New item 2", "is_checked": true}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        habit.ChecklistItems.Should().HaveCount(2);
        habit.ChecklistItems[0].Text.Should().Be("New item 1");
        habit.ChecklistItems[1].IsChecked.Should().BeTrue();
    }

    // ── Update reminder times ──

    [Fact]
    public async Task UpdateReminderTimes_ChangesReminders()
    {
        var habit = CreateHabit("Meds", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "reminder_enabled": true,
            "reminder_times": [5, 30, 60]
        }
        """);

        result.Success.Should().BeTrue();
        habit.ReminderEnabled.Should().BeTrue();
        habit.ReminderTimes.Should().BeEquivalentTo([5, 30, 60]);
    }

    // ── Update description ──

    [Fact]
    public async Task UpdateDescription_ChangesDescription()
    {
        var habit = CreateHabit("Read", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "description": "At least 30 minutes"}""");

        result.Success.Should().BeTrue();
        habit.Description.Should().Be("At least 30 minutes");
    }

    [Fact]
    public async Task UpdateEmoji_ChangesEmoji()
    {
        var habit = CreateHabit("Gym", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "emoji": "💪"}""");

        result.Success.Should().BeTrue();
        habit.Emoji.Should().Be("💪");
    }

    [Fact]
    public async Task ClearEmoji_SetsToNull()
    {
        var habit = CreateHabitWithEmoji("Gym", "💪");
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "emoji": null}""");

        result.Success.Should().BeTrue();
        habit.Emoji.Should().BeNull();
    }

    [Fact]
    public async Task AbsentEmoji_PreservesExisting()
    {
        var habit = CreateHabitWithEmoji("Gym", "💪");
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "title": "Gym Daily"}""");

        result.Success.Should().BeTrue();
        habit.Emoji.Should().Be("💪");
    }

    [Fact]
    public async Task ClearDescription_SetsToNull()
    {
        var habit = CreateHabit("Read", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "description": null}""");

        result.Success.Should().BeTrue();
        habit.Description.Should().BeNull();
    }

    // ── Toggle bad habit ──

    [Fact]
    public async Task ToggleBadHabit_SetsIsBadHabit()
    {
        var habit = CreateHabit("Smoking", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "is_bad_habit": true}""");

        result.Success.Should().BeTrue();
        habit.IsBadHabit.Should().BeTrue();
    }

    // ── Toggle flexible ──

    [Fact]
    public async Task ToggleFlexible_SetsIsFlexible()
    {
        var habit = CreateHabit("Yoga", FrequencyUnit.Week, 3);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "is_flexible": true}""");

        result.Success.Should().BeTrue();
        habit.IsFlexible.Should().BeTrue();
    }

    // ── End date operations ──

    [Fact]
    public async Task SetEndDate_UpdatesEndDate()
    {
        var habit = CreateHabit("Challenge", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "end_date": "2026-12-31"}""");

        result.Success.Should().BeTrue();
        habit.EndDate.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public async Task ClearEndDate_SetsToNull()
    {
        var habit = CreateHabit("Challenge", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "end_date": null}""");

        result.Success.Should().BeTrue();
        habit.EndDate.Should().BeNull();
    }

    // ── Update due date ──

    [Fact]
    public async Task UpdateDueDate_ChangesDueDate()
    {
        var habit = CreateHabit("Task", null, null);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "due_date": "2026-06-15"}""");

        result.Success.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2026, 6, 15));
    }

    // ── Scheduled reminders ──

    [Fact]
    public async Task UpdateScheduledReminders_ReplacesReminders()
    {
        var habit = CreateHabit("Appointment", null, null);
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "scheduled_reminders": [
                {"when": "same_day", "time": "09:00"},
                {"when": "day_before", "time": "18:00"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        habit.ScheduledReminders.Should().HaveCount(2);
    }

    // ── No changes provided ──

    [Fact]
    public async Task NoFieldsProvided_KeepsAllExistingValues()
    {
        var habit = CreateHabit("Original", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeTrue();
        habit.Title.Should().Be("Original");
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        habit.FrequencyQuantity.Should().Be(1);
    }

    // ── Multiple fields at once ──

    [Fact]
    public async Task UpdateMultipleFields_AllFieldsChange()
    {
        var habit = CreateHabit("Old Title", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "title": "New Title",
            "description": "New desc",
            "frequency_unit": "Week",
            "frequency_quantity": 2,
            "is_bad_habit": true,
            "due_date": "2026-05-01",
            "due_time": "10:00",
            "reminder_enabled": true,
            "reminder_times": [15],
            "checklist_items": [{"text": "Item 1"}]
        }
        """);

        result.Success.Should().BeTrue();
        habit.Title.Should().Be("New Title");
        habit.Description.Should().Be("New desc");
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        habit.FrequencyQuantity.Should().Be(2);
        habit.IsBadHabit.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2026, 5, 1));
        habit.DueTime.Should().Be(new TimeOnly(10, 0));
        habit.ReminderEnabled.Should().BeTrue();
        habit.ReminderTimes.Should().BeEquivalentTo([15]);
        habit.ChecklistItems.Should().HaveCount(1);
    }

    // ── Absent optional fields are preserved ──

    [Fact]
    public async Task AbsentDueTime_PreservesExisting()
    {
        var habit = CreateHabitWithTime("Meditate", FrequencyUnit.Day, 1, new TimeOnly(6, 0));
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "title": "Meditate Daily"}""");

        result.Success.Should().BeTrue();
        habit.DueTime.Should().Be(new TimeOnly(6, 0));
    }

    private static Habit CreateHabit(string title, FrequencyUnit? freq, int? qty)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, freq, qty, DueDate: Today)).Value;
    }

    private static Habit CreateHabitWithTime(string title, FrequencyUnit? freq, int? qty, TimeOnly dueTime)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, freq, qty, DueDate: Today, DueTime: dueTime)).Value;
    }

    private static Habit CreateHabitWithEmoji(string title, string emoji)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today, Emoji: emoji)).Value;
    }

    private void SetupHabitFound(Habit habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(habit);
    }

    private void SetupHabitNotFound()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Habit?)null);
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
