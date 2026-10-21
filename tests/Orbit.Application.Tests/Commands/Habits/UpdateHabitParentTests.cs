using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Habits;

public sealed class UpdateHabitParentTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 4, 3);
    private readonly User _user = User.Create("Alex", "parent-update@example.test").Value;
    private readonly List<Habit> _available = [];
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IAppConfigService _config = Substitute.For<IAppConfigService>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IPayGateService _gate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>().PassThroughTransactions<Result>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public UpdateHabitParentTests()
    {
        _habits.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => _available.FirstOrDefault(h => !h.IsDeleted && call.Arg<Expression<Func<Habit, bool>>>().Compile()(h)));
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => _available.Where(h => !h.IsDeleted && call.Arg<Expression<Func<Habit, bool>>>().Compile()(h)).ToList());
        _habits.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => _available.Count(h => !h.IsDeleted && call.Arg<Expression<Func<Habit, bool>>>().Compile()(h)));
        _dates.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        _config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(5);
        _config.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>()).Returns(2);
        _gate.CanCreateHabits(_user.Id, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    private Habit Add(bool general = false, Guid? owner = null)
    {
        var habit = Habit.Create(new HabitCreateParams(owner ?? _user.Id, "Original",
            general ? null : FrequencyUnit.Day, general ? null : 1, Today, IsGeneral: general)).Value;
        _available.Add(habit);
        return habit;
    }

    private UpdateHabitCommand Command(Habit habit) => new(_user.Id, habit.Id, "Changed", "Details",
        habit.FrequencyUnit, habit.FrequencyQuantity, DueDate: habit.DueDate);

    private UpdateHabitCommandHandler Handler(IUnitOfWork? work = null, IPayGateService? gate = null) =>
        new(_habits, Substitute.For<IGenericRepository<SentReminder>>(), Substitute.For<IGenericRepository<Goal>>(),
            gate ?? _gate, Substitute.For<IGoalCompletionService>(), _dates, work ?? _work, _cache,
            Substitute.For<IGenericRepository<Tag>>(), _config);

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task NullParentAndNoClear_PreservesParent(bool? clear)
    {
        var parent = Add();
        var habit = Add();
        habit.SetParentHabitId(parent.Id);
        var result = await Handler().Handle(Command(habit) with { ClearParent = clear }, default);
        result.IsSuccess.Should().BeTrue();
        habit.ParentHabitId.Should().Be(parent.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task SetParent_ThenRepeat_PreservesScheduleAndChecklist(bool? clear)
    {
        var parent = Add();
        var habit = Habit.Create(new HabitCreateParams(_user.Id, "Monthly", FrequencyUnit.Month, 1,
            new DateOnly(2026, 1, 31), DueTime: new TimeOnly(9, 0), ReminderEnabled: true,
            ReminderTimes: [15], ChecklistItems: [new("Same", true), new("Same", false)])).Value;
        habit.Log(new DateOnly(2026, 1, 31)).IsSuccess.Should().BeTrue();
        _available.Add(habit);
        var before = ScheduleSnapshot(habit);
        var command = Command(habit) with
        {
            ParentId = parent.Id,
            ClearParent = clear,
            Options = new(DueTime: habit.DueTime, ChecklistItems: habit.ChecklistItems)
        };
        (await Handler().Handle(command, default)).IsSuccess.Should().BeTrue();
        (await Handler().Handle(command, default)).IsSuccess.Should().BeTrue();
        habit.ParentHabitId.Should().Be(parent.Id);
        ScheduleSnapshot(habit).Should().Be(before);
    }

    [Theory]
    [InlineData("conflict", ErrorCodes.ValidationError)]
    [InlineData("empty", ErrorCodes.TargetParentNotFound)]
    [InlineData("self", ErrorCodes.SelfParent)]
    [InlineData("missing", ErrorCodes.TargetParentNotFound)]
    [InlineData("foreign", ErrorCodes.TargetParentNotFound)]
    [InlineData("deleted", ErrorCodes.TargetParentNotFound)]
    [InlineData("cycle", ErrorCodes.CircularReference)]
    [InlineData("depth", ErrorCodes.MaxDepthReached)]
    [InlineData("general", ErrorCodes.GeneralMismatchWithParent)]
    public async Task InvalidParent_RejectsCompleteEditBeforeMutation(string kind, string code)
    {
        var habit = Add();
        var target = Add(general: kind == "general", owner: kind == "foreign" ? Guid.NewGuid() : null);
        if (kind == "missing") _available.Remove(target);
        if (kind == "deleted") target.SoftDelete();
        if (kind == "cycle") target.SetParentHabitId(habit.Id);
        if (kind == "depth")
        {
            var child = Add();
            child.SetParentHabitId(habit.Id);
            _config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(2);
        }
        var before = JsonSerializer.Serialize(habit);
        var result = await Handler().Handle(Command(habit) with
        {
            ParentId = kind == "empty" ? Guid.Empty : kind == "self" ? habit.Id : target.Id,
            ClearParent = kind == "conflict",
            TagIds = [],
            GoalIds = [],
            Options = new(ChecklistItems: [new("Changed", true)])
        }, default);
        result.ErrorCode.Should().Be(code);
        JsonSerializer.Serialize(habit).Should().Be(before);
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("foreign")]
    public async Task UnavailableSource_UsesGenericOwnershipFailure(string kind)
    {
        var habit = Add(owner: kind == "foreign" ? Guid.NewGuid() : null);
        if (kind == "deleted") habit.SoftDelete();
        var result = await Handler().Handle(Command(habit) with { ClearParent = true }, default);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneralChange_ValidatesFinalParentInsteadOfOldParent(bool detach)
    {
        var oldParent = Add();
        var newParent = Add(general: true);
        var habit = Add();
        habit.SetParentHabitId(oldParent.Id);
        var result = await Handler().Handle(Command(habit) with
        {
            FrequencyUnit = null,
            FrequencyQuantity = null,
            IsGeneral = true,
            ParentId = detach ? null : newParent.Id,
            ClearParent = detach
        }, default);
        result.IsSuccess.Should().BeTrue();
        habit.IsGeneral.Should().BeTrue();
        habit.ParentHabitId.Should().Be(detach ? null : newParent.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneralChange_RejectsIncompatibleRetainedChildren(bool detach)
    {
        var parent = Add();
        var generalParent = Add(general: true);
        var habit = Add();
        habit.SetParentHabitId(parent.Id);
        var child = Add();
        child.SetParentHabitId(habit.Id);
        var result = await Handler().Handle(Command(habit) with
        {
            FrequencyUnit = null,
            FrequencyQuantity = null,
            IsGeneral = true,
            ParentId = detach ? null : generalParent.Id,
            ClearParent = detach
        }, default);
        result.ErrorCode.Should().Be(ErrorCodes.GeneralMismatchWithChildren);
        habit.IsGeneral.Should().BeFalse();
        habit.ParentHabitId.Should().Be(parent.Id);
        habit.Title.Should().Be("Original");
    }

    public static IEnumerable<object[]> MembershipCases =>
        from plan in new[] { "free", "trial", "pro" }
        from wasRoot in new[] { false, true }
        from completed in new[] { false, true }
        from finalRoot in new[] { false, true }
        from reopen in new[] { "none", "end", "start" }
        select new object[] { plan, wasRoot, completed, finalRoot, reopen };

    [Theory]
    [MemberData(nameof(MembershipCases))]
    public async Task AtCapacity_UsesFinalLiveRootMembershipOnce(string plan, bool wasRoot, bool completed, bool finalRoot, string reopen)
    {
        if (plan == "free") _user.StartTrial(DateTime.MinValue);
        if (plan == "pro") _user.GrantLifetimePro();
        var parent = Add();
        var habit = completed
            ? Habit.Create(new HabitCreateParams(_user.Id, "Original", FrequencyUnit.Day, 1,
                Today.AddDays(-2), EndDate: Today.AddDays(-2))).Value
            : Add();
        if (completed)
        {
            _available.Add(habit);
            habit.Log(Today.AddDays(-2)).IsSuccess.Should().BeTrue();
        }
        if (!wasRoot) habit.SetParentHabitId(parent.Id);
        habit.IsCompleted.Should().Be(completed);
        while (_available.Count(h => h.ParentHabitId is null && !h.IsCompleted) < 2) Add();
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        var gate = new PayGateService(_habits, users, _config, _dates);
        var enters = finalRoot && (!completed || reopen != "none") && (!wasRoot || completed);
        var command = Command(habit) with
        {
            ParentId = finalRoot ? null : parent.Id,
            ClearParent = finalRoot,
            ClearEndDate = reopen != "none",
            StartDate = reopen == "start" ? Today : habit.StartDate
        };
        var result = await Handler(gate: gate).Handle(command, default);
        result.IsFailure.Should().Be(enters, result.Error);
        if (enters)
        {
            result.ErrorCode.Should().Be(ErrorCodes.HabitLimitReached);
            habit.Title.Should().Be("Original");
            habit.ParentHabitId.Should().Be(wasRoot ? null : parent.Id);
        }
        else
            habit.ParentHabitId.Should().Be(finalRoot ? null : parent.Id);
        await _habits.Received(enters ? 1 : 0).CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedOneTimePromotion_ChargesOnceOnlyWhenScheduleReopens(bool reopen)
    {
        var parent = Add();
        var habit = Habit.Create(new HabitCreateParams(_user.Id, "Task", null, null, Today,
            ParentHabitId: parent.Id)).Value;
        habit.Log(Today).IsSuccess.Should().BeTrue();
        _available.Add(habit);
        var result = await Handler().Handle(Command(habit) with
        {
            ClearParent = true,
            ClearEndDate = reopen,
            FrequencyUnit = reopen ? FrequencyUnit.Day : null,
            FrequencyQuantity = reopen ? 1 : null
        }, default);
        result.IsSuccess.Should().BeTrue();
        habit.ParentHabitId.Should().BeNull();
        habit.IsCompleted.Should().Be(!reopen);
        await _gate.Received(reopen ? 1 : 0).CanCreateHabits(_user.Id, 1, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpposingUpdatesAndMoves_ShareUserLockAndCannotCreateCycle(bool standalone)
    {
        var first = Add();
        var second = Add();
        var work = new SerializingUnitOfWork();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        _dates.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                prepared.SetResult();
                await release.Task.WaitAsync(call.Arg<CancellationToken>());
            }
            return Today;
        });
        var update = Handler(work).Handle(Command(first) with { ParentId = second.Id }, default);
        await prepared.Task;
        var opposing = standalone
            ? new MoveHabitParentCommandHandler(_habits, work, _config, _gate).Handle(new(_user.Id, second.Id, first.Id), default)
            : Handler(work).Handle(Command(second) with { ParentId = first.Id }, default);
        await work.SecondLockRequested.Task;
        opposing.IsCompleted.Should().BeFalse();
        release.SetResult();
        (await update).IsSuccess.Should().BeTrue();
        (await opposing).ErrorCode.Should().Be(ErrorCodes.CircularReference);
        first.ParentHabitId.Should().Be(second.Id);
        second.ParentHabitId.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentCombinedPromotions_OnlyOneTakesRemainingRootSlot()
    {
        var parent = Add();
        var first = Add();
        var second = Add();
        first.SetParentHabitId(parent.Id);
        second.SetParentHabitId(parent.Id);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        var gate = new PayGateService(_habits, users, _config, _dates);
        var work = new SerializingUnitOfWork();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        _dates.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                prepared.SetResult();
                await release.Task.WaitAsync(call.Arg<CancellationToken>());
            }
            return Today;
        });
        var update = Handler(work, gate).Handle(Command(first) with { ClearParent = true }, default);
        await prepared.Task;
        var opposing = Handler(work, gate).Handle(Command(second) with { ClearParent = true }, default);
        await work.SecondLockRequested.Task;
        opposing.IsCompleted.Should().BeFalse();
        release.SetResult();
        (await update).IsSuccess.Should().BeTrue();
        (await opposing).ErrorCode.Should().Be(ErrorCodes.HabitLimitReached);
        first.ParentHabitId.Should().BeNull();
        second.ParentHabitId.Should().Be(parent.Id);
    }

    private static string ScheduleSnapshot(Habit habit) => JsonSerializer.Serialize(new
    {
        habit.FrequencyUnit,
        habit.FrequencyQuantity,
        habit.DueDate,
        habit.StartDate,
        habit.ScheduledStartDate,
        habit.OriginalDayOfMonth,
        habit.IntervalWeeks,
        habit.Days,
        habit.DueTime,
        habit.ReminderEnabled,
        habit.ReminderTimes,
        habit.ScheduledReminders,
        habit.RelativeReminders,
        habit.ChecklistItems,
        Logs = habit.Logs.Select(l => new { l.Id, l.Date, l.Value })
    });

    public void Dispose() => _cache.Dispose();
}
