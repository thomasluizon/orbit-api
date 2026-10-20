using System.Linq.Expressions;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Goals.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Chat.Tools;

public class UpdateHabitToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly UpdateHabitTool _tool;
    private readonly IGenericRepository<SentReminder> _sentReminders = Substitute.For<IGenericRepository<SentReminder>>();

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public UpdateHabitToolTests()
    {
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        ConfigureTransaction(_unitOfWork);
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _tool = CreateTool(_habitRepo, _unitOfWork, _payGate);
    }

    [Fact]
    public async Task DueTimeChange_ClearsOnlyTodaysSentReminder()
    {
        var habit = CreateHabitWithTime("Read", FrequencyUnit.Day, 1, new TimeOnly(9, 0));
        SetupHabitFound(habit);
        var reminders = _sentReminders;
        var today = SentReminder.Create(habit.Id, Today, 15);
        var yesterday = SentReminder.Create(habit.Id, Today.AddDays(-1), 15);
        reminders.FindAsync(Arg.Any<Expression<Func<SentReminder, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { today, yesterday }.Where(call.Arg<Expression<Func<SentReminder, bool>>>().Compile()).ToList());
        var result = await Execute($$"""{"habit_id":"{{habit.Id}}","due_time":"10:00"}""");
        result.Success.Should().BeTrue();
        reminders.Received(1).Remove(today);
        reminders.DidNotReceive().Remove(yesterday);
    }

    [Fact]
    public async Task ParentGeneralFlag_CannotDivergeFromActiveChildren()
    {
        var parent = CreateHabit("Parent", null, null);
        var child = Habit.Create(new HabitCreateParams(UserId, "Child", FrequencyUnit.Day, 1,
            Today, ParentHabitId: parent.Id)).Value;
        SetupHabitFound(parent);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { parent, child }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        var result = await Execute($$"""{"habit_id":"{{parent.Id}}","is_general":true}""");
        result.Success.Should().BeFalse();
        result.Error.Should().Be(Orbit.Application.Common.ErrorMessages.GeneralMismatchWithChildren.Message);
        parent.IsGeneral.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("due_end_time", "\"15:00\"")]
    [InlineData("is_general", "true")]
    [InlineData("slip_alert_enabled", "true")]
    [InlineData("relative_reminders", "[{\"minutes_before\":30}]")]
    public async Task NewFields_UpdateStoredValues(string field, string value)
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Read", null, null, Today,
            DueTime: new TimeOnly(9, 0))).Value;
        SetupHabitFound(habit);
        _payGate.CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await Execute($$"""{"habit_id":"{{habit.Id}}","{{field}}":{{value}}}""");
        result.Success.Should().BeTrue();
        switch (field)
        {
            case "due_end_time": habit.DueEndTime.Should().Be(new TimeOnly(15, 0)); break;
            case "is_general": habit.IsGeneral.Should().BeTrue(); break;
            case "slip_alert_enabled": habit.SlipAlertEnabled.Should().BeTrue(); break;
            case "relative_reminders": habit.RelativeReminders.Should().Equal(new RelativeReminderTime(MinutesBefore: 30)); break;
        }
    }

    [Fact]
    public async Task ChildGeneralFlag_CannotDivergeFromParent()
    {
        var parent = CreateHabit("Parent", null, null);
        var child = Habit.Create(new HabitCreateParams(UserId, "Child", null, null, Today, ParentHabitId: parent.Id)).Value;
        _habitRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()(child) ? child : parent);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([parent]);
        var result = await Execute($$"""{"habit_id":"{{child.Id}}","is_general":true}""");
        result.Success.Should().BeFalse();
        child.IsGeneral.Should().BeFalse();
    }

    [Theory]
    [InlineData("[{\"minutes_before\":30,\"when\":\"same_day\",\"time\":\"08:00\"}]")]
    [InlineData("[{\"minutes_before\":-1440}]")]
    [InlineData("[{\"when\":\"same_day\",\"time\":\"25:00\"}]")]
    [InlineData("[{\"minutes_before\":30},{\"minutes_before\":30}]")]
    public async Task InvalidRelativeReminders_FailBeforeMutation(string reminders)
    {
        var habit = CreateHabitWithTime("Read", FrequencyUnit.Day, 1, new TimeOnly(9, 0));
        SetupHabitFound(habit);
        var result = await Execute($$"""{"habit_id":"{{habit.Id}}","relative_reminders":{{reminders}}}""");
        result.Success.Should().BeFalse();
        habit.RelativeReminders.Should().BeEmpty();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnablingSlipAlerts_RequiresPro()
    {
        var habit = CreateHabit("Read", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);
        _payGate.CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Pro required"));
        var result = await Execute($$"""{"habit_id":"{{habit.Id}}","slip_alert_enabled":true}""");
        result.Success.Should().BeFalse();
        habit.SlipAlertEnabled.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DueEndTime_ExplicitNullClearsDeadline()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Read", null, null, Today,
            DueEndTime: new TimeOnly(15, 0))).Value;
        SetupHabitFound(habit);
        var result = await Execute($$"""{"habit_id":"{{habit.Id}}","due_end_time":null}""");
        result.Success.Should().BeTrue();
        habit.DueEndTime.Should().BeNull();
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
    public async Task AtCapReactivation_ReturnsPayGateFailureWithoutMutatingHabit()
    {
        var habit = CreateCompletedRecurringHabit();
        SetupHabitFound(habit);
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Habit limit reached"));
        var tool = CreateTool(_habitRepo, _unitOfWork, payGate);

        var args = JsonDocument.Parse($$$"""{"habit_id": "{{{habit.Id}}}", "end_date": null}""").RootElement;
        var result = await tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        habit.IsCompleted.Should().BeTrue();
        habit.EndDate.Should().Be(Today);
    }

    [Fact]
    public async Task AllowedReactivation_UpdatesCompletedHabit()
    {
        var habit = CreateCompletedRecurringHabit();
        SetupHabitFound(habit);
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = CreateTool(_habitRepo, _unitOfWork, payGate);

        var args = JsonDocument.Parse($$$"""{"habit_id": "{{{habit.Id}}}", "end_date": null}""").RootElement;
        var result = await tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
        habit.EndDate.Should().BeNull();
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
        var relativeReminders = new List<RelativeReminderTime>
        {
            new(MinutesBefore: 30),
            new(When: ScheduledReminderWhen.DayBefore, Time: new TimeOnly(18, 0))
        };
        var checklist = new List<ChecklistItem> { new("Fill bottle", true) };
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Water", FrequencyUnit.Day, 1, Today,
            Description: "Two litres", Days: [DayOfWeek.Monday, DayOfWeek.Wednesday],
            IsBadHabit: true, DueTime: new TimeOnly(9, 0), DueEndTime: new TimeOnly(15, 0),
            ReminderEnabled: true, ReminderTimes: [10, 20], SlipAlertEnabled: true,
            ChecklistItems: checklist, EndDate: Today.AddDays(30), Emoji: "💧", IntervalWeeks: 2,
            RelativeReminders: relativeReminders)).Value;
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "title": "Drink Water"}""");

        result.Success.Should().BeTrue();
        habit.Title.Should().Be("Drink Water");
        habit.Description.Should().Be("Two litres");
        habit.Emoji.Should().Be("💧");
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        habit.FrequencyQuantity.Should().Be(1);
        habit.IntervalWeeks.Should().Be(2);
        habit.Days.Should().BeEquivalentTo([DayOfWeek.Monday, DayOfWeek.Wednesday]);
        habit.IsBadHabit.Should().BeTrue();
        habit.DueDate.Should().Be(Today);
        habit.DueTime.Should().Be(new TimeOnly(9, 0));
        habit.DueEndTime.Should().Be(new TimeOnly(15, 0));
        habit.ReminderEnabled.Should().BeTrue();
        habit.ReminderTimes.Should().Equal(10, 20);
        habit.SlipAlertEnabled.Should().BeTrue();
        habit.ChecklistItems.Should().Equal(checklist);
        habit.EndDate.Should().Be(Today.AddDays(30));
        habit.RelativeReminders.Should().Equal(relativeReminders);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"emoji":"📚"}""")]
    [InlineData("""{"due_date":"2026-06-15"}""")]
    [InlineData("""{"due_time":null}""")]
    public async Task PartialUpdate_KeepsDueEndTime(string updates)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Read", FrequencyUnit.Day, 1, Today,
            DueTime: new TimeOnly(9, 0), DueEndTime: new TimeOnly(15, 0))).Value;
        SetupHabitFound(habit);
        var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(updates)!;
        fields["habit_id"] = habit.Id;

        var result = await Execute(JsonSerializer.Serialize(fields));

        result.Success.Should().BeTrue();
        habit.DueEndTime.Should().Be(new TimeOnly(15, 0));
    }

    [Fact]
    public async Task UpdateTitleOnly_WithoutDueTime_KeepsDeadlineAndScheduledReminders()
    {
        var scheduledReminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.SameDay, new TimeOnly(14, 0))
        };
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Read", null, null, Today, DueEndTime: new TimeOnly(15, 0),
            ReminderEnabled: true, ScheduledReminders: scheduledReminders)).Value;
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id":"{{{habit.Id}}}","title":"Read daily"}""");

        result.Success.Should().BeTrue();
        habit.Title.Should().Be("Read daily");
        habit.DueTime.Should().BeNull();
        habit.DueEndTime.Should().Be(new TimeOnly(15, 0));
        habit.ScheduledReminders.Should().Equal(scheduledReminders);
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
    public async Task UpdateIntervalWeeks_ChangesInterval()
    {
        var habit = CreateHabit("Exercise", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "interval_weeks": 3}""");

        result.Success.Should().BeTrue();
        habit.IntervalWeeks.Should().Be(3);
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

    [Fact]
    public async Task ToggleBadHabit_SetsIsBadHabit()
    {
        var habit = CreateHabit("Smoking", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "is_bad_habit": true}""");

        result.Success.Should().BeTrue();
        habit.IsBadHabit.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleFlexible_SetsIsFlexible()
    {
        var habit = CreateHabit("Yoga", FrequencyUnit.Week, 3);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "is_flexible": true}""");

        result.Success.Should().BeTrue();
        habit.IsFlexible.Should().BeTrue();
    }

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

    [Fact]
    public async Task UpdateDueDate_ChangesDueDate()
    {
        var habit = CreateHabit("Task", null, null);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "due_date": "2026-06-15"}""");

        result.Success.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2026, 6, 15));
    }

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

    [Fact]
    public async Task UpdateDueTimedHabit_WithScheduledReminders_PreservesClockTimeInRelativeStore()
    {
        var habit = CreateHabitWithTime("Standup", FrequencyUnit.Day, 1, new TimeOnly(9, 0));
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "reminder_enabled": true,
            "scheduled_reminders": [
                {"when": "same_day", "time": "08:30"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        habit.RelativeReminders.Should().ContainSingle(r =>
            r.When == ScheduledReminderWhen.SameDay && r.Time == new TimeOnly(8, 30));
        habit.ScheduledReminders.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDueTimedHabit_WithDayBeforeScheduledReminder_PreservesClockTime()
    {
        var habit = CreateHabitWithTime("Standup", FrequencyUnit.Day, 1, new TimeOnly(9, 0));
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "reminder_enabled": true,
            "scheduled_reminders": [
                {"when": "day_before", "time": "21:00"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        habit.RelativeReminders.Should().ContainSingle(r =>
            r.When == ScheduledReminderWhen.DayBefore && r.Time == new TimeOnly(21, 0));
        habit.ScheduledReminders.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDueTimedHabit_WithReminderTimes_ClearsStaleScheduledReminders()
    {
        var habit = CreateHabitWithTimeAndScheduledReminders(
            "Standup", new TimeOnly(9, 0),
            new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8, 30)));
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "reminder_enabled": true,
            "reminder_times": [30]
        }
        """);

        result.Success.Should().BeTrue();
        habit.ReminderTimes.Should().BeEquivalentTo([30]);
        habit.ScheduledReminders.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateNoDueTimeHabit_AddDueTime_FoldsScheduledReminders()
    {
        var habit = CreateHabitWithScheduledReminders(
            "Appointment",
            new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8, 0)));
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "due_time": "09:00"}""");

        result.Success.Should().BeTrue();
        habit.DueTime.Should().Be(new TimeOnly(9, 0));
        habit.ReminderTimes.Should().BeEmpty();
        habit.RelativeReminders.Should().ContainSingle(r =>
            r.When == ScheduledReminderWhen.SameDay && r.Time == new TimeOnly(8, 0));
        habit.ScheduledReminders.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDueTimedHabit_ClearDueTime_ConvertsReminderTimesToScheduledReminders()
    {
        var habit = CreateHabitWithTimeAndReminderTimes("Standup", new TimeOnly(9, 0), 30);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "due_time": null}""");

        result.Success.Should().BeTrue();
        habit.DueTime.Should().BeNull();
        habit.ReminderTimes.Should().BeEmpty();
        habit.ScheduledReminders.Should().ContainSingle(reminder =>
            reminder.When == ScheduledReminderWhen.SameDay && reminder.Time == new TimeOnly(8, 30));
    }

    [Fact]
    public async Task UpdateDueTimedHabit_SameDayReminderAfterDueTime_PreservesLocalTime()
    {
        var habit = CreateHabitWithTime("Standup", FrequencyUnit.Day, 1, new TimeOnly(9, 0));
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "reminder_enabled": true,
            "scheduled_reminders": [
                {"when": "same_day", "time": "10:00"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        habit.ReminderTimes.Should().NotContain(0);
        habit.ScheduledReminders.Should().BeEmpty();
        habit.RelativeReminders.Should().ContainSingle(reminder =>
            reminder.When == ScheduledReminderWhen.SameDay && reminder.Time == new TimeOnly(10, 0));
    }

    [Fact]
    public async Task UpdateNoDueTimeHabit_KeepsScheduledReminderStore()
    {
        var habit = CreateHabit("Appointment", null, null);
        SetupHabitFound(habit);

        var result = await Execute($$$"""
        {
            "habit_id": "{{{habit.Id}}}",
            "reminder_enabled": true,
            "scheduled_reminders": [
                {"when": "same_day", "time": "09:00"},
                {"when": "day_before", "time": "18:00"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        habit.ScheduledReminders.Should().HaveCount(2);
        habit.ReminderTimes.Should().BeEquivalentTo([15]);
    }

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

    [Fact]
    public async Task AbsentDueTime_PreservesExisting()
    {
        var habit = CreateHabitWithTime("Meditate", FrequencyUnit.Day, 1, new TimeOnly(6, 0));
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "title": "Meditate Daily"}""");

        result.Success.Should().BeTrue();
        habit.DueTime.Should().Be(new TimeOnly(6, 0));
    }

    [Fact]
    public async Task WrongUser_CannotUpdateAnothersHabit_OwnerCan_RealContext()
    {
        var databaseName = $"UpdateHabitIsolation_{Guid.NewGuid()}";
        Guid habitId;
        await using (var seed = CreateContext(databaseName))
        {
            var ownerHabit = CreateHabit("Original title", FrequencyUnit.Day, 1);
            seed.Habits.Add(ownerHabit);
            await seed.SaveChangesAsync();
            habitId = ownerHabit.Id;
        }

        await using var context = CreateContext(databaseName);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        ConfigureTransaction(unitOfWork);
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => context.SaveChangesAsync(call.Arg<CancellationToken>()));
        var tool = CreateTool(new GenericRepository<Habit>(context), unitOfWork, _payGate);

        var attackerId = Guid.NewGuid();
        var attackerResult = await tool.ExecuteAsync(RenameArgs(habitId, "Hijacked"), attackerId, CancellationToken.None);
        await context.SaveChangesAsync();

        attackerResult.Success.Should().BeFalse();
        attackerResult.Error.Should().Contain("not found");
        await using (var afterAttack = CreateContext(databaseName))
            (await afterAttack.Habits.SingleAsync(h => h.Id == habitId)).Title
                .Should().Be("Original title", "a foreign user must not rename another user's habit");

        var ownerResult = await tool.ExecuteAsync(RenameArgs(habitId, "Owner rename"), UserId, CancellationToken.None);
        await context.SaveChangesAsync();

        ownerResult.Success.Should().BeTrue("the owner can update their own habit");
        await using (var afterOwner = CreateContext(databaseName))
            (await afterOwner.Habits.SingleAsync(h => h.Id == habitId)).Title.Should().Be("Owner rename");
    }

    private static JsonElement RenameArgs(Guid habitId, string title) =>
        JsonDocument.Parse($$"""{"habit_id":"{{habitId}}","title":"{{title}}"}""").RootElement;

    private UpdateHabitTool CreateTool(IGenericRepository<Habit> repository, IUnitOfWork unitOfWork, IPayGateService gate)
    {
        var mediator = Substitute.For<IMediator>();
        var handler = new UpdateHabitCommandHandler(repository, _sentReminders, Substitute.For<IGenericRepository<Goal>>(),
            gate, Substitute.For<IGoalCompletionService>(), _userDateService, unitOfWork,
            new MemoryCache(new MemoryCacheOptions()));
        mediator.Send(Arg.Any<UpdateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var command = call.Arg<UpdateHabitCommand>();
            var validation = await new UpdateHabitCommandValidator(repository).ValidateAsync(command, call.Arg<CancellationToken>());
            return validation.IsValid ? await handler.Handle(command, call.Arg<CancellationToken>()) : Result.Failure(validation.ToString());
        });
        return new UpdateHabitTool(repository, _userDateService, mediator);
    }

    private static void ConfigureTransaction(IUnitOfWork unitOfWork)
    {
        unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result>>>(0)(
                call.ArgAt<CancellationToken>(1)));
    }

    private static OrbitDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(databaseName).Options);

    private static Habit CreateHabit(string title, FrequencyUnit? freq, int? qty)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, freq, qty, DueDate: Today)).Value;
    }

    private static Habit CreateCompletedRecurringHabit()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Finished recurring habit", FrequencyUnit.Day, 1, DueDate: Today, EndDate: Today)).Value;
        habit.AdvanceDueDate(Today);
        return habit;
    }

    private static Habit CreateHabitWithTime(string title, FrequencyUnit? freq, int? qty, TimeOnly dueTime)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, freq, qty, DueDate: Today, DueTime: dueTime)).Value;
    }

    private static Habit CreateHabitWithEmoji(string title, string emoji)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today, Emoji: emoji)).Value;
    }

    private static Habit CreateHabitWithTimeAndScheduledReminders(
        string title, TimeOnly dueTime, params ScheduledReminderTime[] scheduledReminders)
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1, DueDate: Today,
            DueTime: dueTime, ScheduledReminders: scheduledReminders)).Value;
    }

    private static Habit CreateHabitWithScheduledReminders(
        string title, params ScheduledReminderTime[] scheduledReminders)
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1, DueDate: Today,
            ScheduledReminders: scheduledReminders)).Value;
    }

    private static Habit CreateHabitWithTimeAndReminderTimes(
        string title, TimeOnly dueTime, params int[] reminderTimes)
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1, DueDate: Today,
            DueTime: dueTime, ReminderEnabled: true, ReminderTimes: reminderTimes)).Value;
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
