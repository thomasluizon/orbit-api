using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using System.Linq.Expressions;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Commands.Habits;

public class UpdateHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<SentReminder> _sentReminderRepo = Substitute.For<IGenericRepository<SentReminder>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGoalCompletionService _goalCompletionService = Substitute.For<IGoalCompletionService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly UpdateHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public UpdateHabitCommandHandlerTests()
    {
        _payGate.CanUseSlipAlerts(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _handler = new UpdateHabitCommandHandler(
            _habitRepo, _sentReminderRepo, _goalRepo, _payGate, _goalCompletionService,
            _userDateService, _unitOfWork, _cache);
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result>>>(0)(
                call.ArgAt<CancellationToken>(1)));

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
    }

    private static Habit CreateTestHabit(Guid? userId = null)
    {
        return Habit.Create(new HabitCreateParams(
            userId ?? UserId, "Original Title", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
    }

    private static Habit CreateCompletedRecurringHabit()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Completed recurring habit", FrequencyUnit.Day, 1,
            DueDate: Today, EndDate: Today)).Value;
        habit.Log(Today).IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeTrue();
        return habit;
    }

    [Fact]
    public async Task Handle_ExplicitStart_PassesAccountDayAndRetainsHistory()
    {
        var habit = CreateTestHabit();
        habit.Log(Today).IsSuccess.Should().BeTrue();
        _habitRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habit);
        var command = new UpdateHabitCommand(UserId, habit.Id, "Read", null, FrequencyUnit.Day, 1,
            StartDate: Today.AddDays(-1));
        (await _handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();
        habit.StartDate.Should().Be(Today.AddDays(-1));
        habit.DueDate.Should().Be(Today.AddDays(1));
        habit.Logs.Should().ContainSingle();
        (await _handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(Today.AddDays(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_EndDateReactivation_AllowanceRefused_LeavesHabitUnchanged(bool clearEndDate)
    {
        var habit = CreateCompletedRecurringHabit();
        var originalDueDate = habit.DueDate;
        var originalUpdatedAtUtc = habit.UpdatedAtUtc;
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habit);
        var refusal = Result.Failure("Reactivation refused.", ErrorCodes.HabitLimitReached);
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>())
            .Returns(refusal);
        DateOnly? endDate = clearEndDate ? null : Today.AddDays(7);
        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated title", "Updated description", FrequencyUnit.Day, 1,
            ClearEndDate: clearEndDate,
            Options: new UpdateHabitCommandOptions(EndDate: endDate));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Should().BeSameAs(refusal);
        result.ErrorCode.Should().Be(ErrorCodes.HabitLimitReached);
        habit.IsCompleted.Should().BeTrue();
        habit.EndDate.Should().Be(Today);
        habit.DueDate.Should().Be(originalDueDate);
        habit.Title.Should().Be("Completed recurring habit");
        habit.Description.Should().BeNull();
        habit.UpdatedAtUtc.Should().Be(originalUpdatedAtUtc);
        await _payGate.Received(1).CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_EndDateReactivation_AllowanceGranted_UpdatesAndSaves(bool clearEndDate)
    {
        var habit = CreateCompletedRecurringHabit();
        var originalDueDate = habit.DueDate;
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habit);
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        DateOnly? endDate = clearEndDate ? null : Today.AddDays(7);
        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated title", "Updated description", FrequencyUnit.Day, 1,
            ClearEndDate: clearEndDate,
            Options: new UpdateHabitCommandOptions(EndDate: endDate));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
        habit.EndDate.Should().Be(endDate);
        habit.DueDate.Should().Be(originalDueDate);
        habit.Title.Should().Be("Updated title");
        habit.Description.Should().Be("Updated description");
        await _payGate.Received(1).CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TitleUpdateWithoutReactivation_DoesNotAskForAllowance()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habit);
        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Reactivation refused.", ErrorCodes.HabitLimitReached));
        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated title", habit.Description, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.Title.Should().Be("Updated title");
        habit.IsCompleted.Should().BeFalse();
        habit.EndDate.Should().BeNull();
        await _payGate.DidNotReceive().CanCreateHabits(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IsGeneralReactivationAtCeiling_IsRefusedWithoutMutation()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Completed task", null, null, DueDate: Today)).Value;
        habit.Log(Today).IsSuccess.Should().BeTrue();
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habit);
        _habitRepo.FindAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Habit>());
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("You've reached the 1000 habit limit."));
        var command = new UpdateHabitCommand(
            UserId,
            habit.Id,
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            IsGeneral: true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("You've reached the 1000 habit limit.");
        habit.IsCompleted.Should().BeTrue();
        habit.IsGeneral.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ValidCommand_UpdatesAndSaves()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated Title", "New description",
            FrequencyUnit.Week, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.Title.Should().Be("Updated Title");
        habit.Description.Should().Be("New description");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task Handle_SlipAlerts_OnlyEnablingRequiresPro(
        bool enabled, bool? requested, bool hasProAccess)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Original Title", FrequencyUnit.Day, 1, Today,
            SlipAlertEnabled: enabled)).Value;
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habit);
        _payGate.CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>())
            .Returns(hasProAccess ? Result.Success() : Result.PayGateFailure("Pro required"));
        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated Title", null, FrequencyUnit.Day, 1,
            Options: new UpdateHabitCommandOptions(SlipAlertEnabled: requested));
        var requiresPro = requested == true && !enabled;

        var result = await _handler.Handle(command, CancellationToken.None);

        if (requiresPro && !hasProAccess)
        {
            result.ErrorCode.Should().Be(Result.PayGateErrorCode);
            habit.Title.Should().Be("Original Title");
            habit.SlipAlertEnabled.Should().BeFalse();
            await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        }
        else
        {
            result.IsSuccess.Should().BeTrue();
            habit.Title.Should().Be("Updated Title");
            habit.SlipAlertEnabled.Should().Be(requested ?? enabled);
            await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        }

        await _payGate.Received(requiresPro ? 1 : 0)
            .CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("unchanged", false)]
    [InlineData("reordered", false)]
    [InlineData("clock-equivalent", false)]
    [InlineData("offset-equivalent", false)]
    [InlineData("due-time", true)]
    [InlineData("clear-due-time", true)]
    [InlineData("enabled", true)]
    [InlineData("offsets", true)]
    [InlineData("relative", true)]
    [InlineData("clock", true)]
    public async Task Handle_ReminderSchedule_ClearsSentRowsOnlyWhenChanged(string change, bool clears)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Timed habit", FrequencyUnit.Day, 1, Today,
            DueTime: new TimeOnly(9, 0), ReminderEnabled: true,
            ReminderTimes: [15, 30],
            RelativeReminders: [new(MinutesBefore: 60),
                new(When: ScheduledReminderWhen.SameDay, Time: new TimeOnly(8, 0))])).Value;
        var sent = SentReminder.Create(habit.Id, Today, 15);
        var yesterday = SentReminder.Create(habit.Id, Today.AddDays(-1), 15);
        var otherHabit = SentReminder.Create(Guid.NewGuid(), Today, 15);
        SentReminder[] rows = [sent, yesterday, otherHabit];
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habit);
        _sentReminderRepo.FindAsync(
                Arg.Any<Expression<Func<SentReminder, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => rows.Where(call.ArgAt<Expression<Func<SentReminder, bool>>>(0).Compile()).ToList());
        var options = new UpdateHabitCommandOptions(
            DueTime: habit.DueTime, ReminderEnabled: habit.ReminderEnabled,
            ReminderTimes: habit.ReminderTimes, RelativeReminders: habit.RelativeReminders);
        options = change switch
        {
            "reordered" => options with
            {
                ReminderTimes = [30, 15],
                RelativeReminders = habit.RelativeReminders.Reverse().ToList()
            },
            "clock-equivalent" => options with
            {
                RelativeReminders = null,
                ScheduledReminders = [new(ScheduledReminderWhen.SameDay, new TimeOnly(8, 0))]
            },
            "offset-equivalent" => options with
            {
                RelativeReminders = habit.RelativeReminders.Concat([new RelativeReminderTime(MinutesBefore: 15)]).ToList()
            },
            "due-time" => options with { DueTime = new TimeOnly(11, 0) },
            "clear-due-time" => options with { DueTime = null },
            "enabled" => options with { ReminderEnabled = false },
            "offsets" => options with { ReminderTimes = [10] },
            "relative" => options with { RelativeReminders = [new(MinutesBefore: 90)] },
            "clock" => options with
            {
                RelativeReminders = null,
                ScheduledReminders = [new(ScheduledReminderWhen.SameDay, new TimeOnly(7, 0))]
            },
            _ => options
        };
        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Renamed habit", null, FrequencyUnit.Day, 1, Options: options);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _sentReminderRepo.Received(clears ? 1 : 0).Remove(sent);
        _sentReminderRepo.DidNotReceive().Remove(yesterday);
        _sentReminderRepo.DidNotReceive().Remove(otherHabit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_UntimedReminderSchedule_ClearsSentRowsOnlyWhenChanged(bool changed)
    {
        var clock = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8, 0));
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Untimed habit", FrequencyUnit.Day, 1, Today,
            ReminderEnabled: true, ScheduledReminders: [clock])).Value;
        var sent = SentReminder.Create(habit.Id, Today, 0);
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habit);
        _sentReminderRepo.FindAsync(
                Arg.Any<Expression<Func<SentReminder, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { sent });
        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Renamed habit", null, FrequencyUnit.Day, 1,
            Options: new UpdateHabitCommandOptions(
                ScheduledReminders: [changed ? clock with { Time = new TimeOnly(7, 0) } : clock]));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _sentReminderRepo.Received(changed ? 1 : 0).Remove(sent);
    }

    [Fact]
    public async Task Handle_TimedHabitScheduledReminderEdit_SavesFoldedClockReminder()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Timed habit", FrequencyUnit.Day, 1, Today,
            DueTime: new TimeOnly(9, 0),
            ScheduledReminders: [new(ScheduledReminderWhen.SameDay, new TimeOnly(8, 0))])).Value;
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habit);
        _sentReminderRepo.FindAsync(
                Arg.Any<Expression<Func<SentReminder, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SentReminder>());
        var command = new UpdateHabitCommand(
            UserId, habit.Id, habit.Title, habit.Description, habit.FrequencyUnit, habit.FrequencyQuantity,
            Options: new UpdateHabitCommandOptions(
                DueTime: habit.DueTime,
                ScheduledReminders: [new(ScheduledReminderWhen.SameDay, new TimeOnly(10, 0))]));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.ScheduledReminders.Should().BeEmpty();
        habit.RelativeReminders.Should().ContainSingle().Which.Should().Be(
            new RelativeReminderTime(When: ScheduledReminderWhen.SameDay, Time: new TimeOnly(10, 0)));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithIntervalWeeks_UpdatesInterval()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new UpdateHabitCommand(
            UserId,
            habit.Id,
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            IntervalWeeks: 3);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.IntervalWeeks.Should().Be(3);
    }

    [Fact]
    public async Task Handle_LegacyPayloadWithoutIntervalWeeks_PreservesInterval()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Alternating week habit",
            FrequencyUnit.Day,
            1,
            DueDate: Today,
            IntervalWeeks: 2)).Value;
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new UpdateHabitCommand(
            UserId,
            habit.Id,
            "Updated title",
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.IntervalWeeks.Should().Be(2);
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var command = new UpdateHabitCommand(
            UserId, Guid.NewGuid(), "Title", null, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotFound.Message);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsFailure()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var command = new UpdateHabitCommand(
            Guid.NewGuid(), Guid.NewGuid(), "Title", null, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotFound.Message);
    }

    [Fact]
    public async Task Handle_InvalidTitle_ReturnsFailure()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "", null, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title");
    }

    [Fact]
    public async Task Handle_DueTimeChanged_ClearsSentReminder()
    {
        var habit = CreateTestHabit();
        var reminder = SentReminder.Create(habit.Id, Today, 15);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _sentReminderRepo.FindAsync(
            Arg.Any<Expression<Func<SentReminder, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<SentReminder> { reminder }.AsReadOnly());

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1,
            Options: new UpdateHabitCommandOptions(DueTime: new TimeOnly(14, 30)));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _sentReminderRepo.Received(1).Remove(reminder);
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var cacheKey = $"summary:{UserId}:{Today:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ForeignGoalId_ReturnsFailureWithoutSaving()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal>());

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1,
            GoalIds: new List<Guid> { Guid.NewGuid() });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorCodes.GoalNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyGoalIds_ClearsLinksAndSaves()
    {
        var habit = CreateTestHabit();
        var existingGoal = Goal.Create(UserId, "Existing", 10, "km").Value;
        habit.AddGoal(existingGoal);
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1,
            GoalIds: new List<Guid>());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.Goals.Should().BeEmpty();
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IsGeneralMatchesParent_Succeeds()
    {
        var parent = CreateTestHabit();
        var habit = CreateTestHabit();
        habit.SetParentHabitId(parent.Id);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit, parent);

        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([parent]);

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1,
            IsGeneral: parent.IsGeneral);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.IsGeneral.Should().Be(parent.IsGeneral);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IsGeneralMismatchesParent_ReturnsFailureWithoutSaving()
    {
        var parent = CreateTestHabit();
        var habit = CreateTestHabit();
        habit.SetParentHabitId(parent.Id);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit, parent);

        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([parent]);

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1,
            IsGeneral: !parent.IsGeneral);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorCodes.GeneralMismatchWithParent);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IsGeneralMatchesAllChildren_Succeeds()
    {
        var habit = CreateTestHabit();
        var child = CreateTestHabit();
        child.SetParentHabitId(habit.Id);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { child }.AsReadOnly());

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1,
            IsGeneral: child.IsGeneral);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.IsGeneral.Should().Be(child.IsGeneral);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IsGeneralMismatchesChild_ReturnsFailureWithoutSaving()
    {
        var habit = CreateTestHabit();
        var child = CreateTestHabit();
        child.SetParentHabitId(habit.Id);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { child }.AsReadOnly());

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1,
            IsGeneral: !child.IsGeneral);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorCodes.GeneralMismatchWithChildren);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IsGeneralMismatchesCompletedOneTimeTaskChild_Succeeds()
    {
        var habit = CreateTestHabit();
        var completedChild = Habit.Create(new HabitCreateParams(
            UserId, "Completed one-time child", null, null, DueDate: Today)).Value;
        completedChild.SetParentHabitId(habit.Id);
        completedChild.Log(Today);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { completedChild }.AsReadOnly());

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, null, null,
            IsGeneral: !completedChild.IsGeneral);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.IsGeneral.Should().Be(!completedChild.IsGeneral);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IsGeneralMismatchesIncompleteOneTimeTaskChild_ReturnsFailureWithoutSaving()
    {
        var habit = CreateTestHabit();
        var incompleteChild = Habit.Create(new HabitCreateParams(
            UserId, "Incomplete one-time child", null, null, DueDate: Today)).Value;
        incompleteChild.SetParentHabitId(habit.Id);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { incompleteChild }.AsReadOnly());

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1,
            IsGeneral: !incompleteChild.IsGeneral);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorCodes.GeneralMismatchWithChildren);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
