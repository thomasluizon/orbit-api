using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Commands.Habits;

public sealed class BadHabitParentSlipTests : IAsyncDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private readonly Guid _userId = Guid.NewGuid();
    private readonly OrbitDbContext _context = new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IGamificationService _gamification = Substitute.For<IGamificationService>();
    private readonly IUserStreakService _streaks = Substitute.For<IUserStreakService>();
    private readonly IGoalCompletionService _goals = Substitute.For<IGoalCompletionService>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly GenericRepository<Habit> _habits;
    private readonly LogHabitCommandHandler _single;
    private readonly BulkLogHabitsCommandHandler _bulk;
    private readonly LogHabitTool _logTool;
    private readonly UnlogHabitTool _unlogTool;

    public BadHabitParentSlipTests()
    {
        _habits = new GenericRepository<Habit>(_context);
        var logs = new GenericRepository<HabitLog>(_context);
        var mediator = Substitute.For<IMediator>();
        var payGate = Substitute.For<IPayGateService>();
        _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Today);
        _work.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => _context.SaveChangesAsync(call.Arg<CancellationToken>()));
        PassThrough<LogHabitResponse>();
        PassThrough<LogHabitCommandHandler.PersistedLog>();
        PassThrough<ToolResult>();
        _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _goals.SyncDerivedGoalsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                await _context.SaveChangesAsync(call.Arg<CancellationToken>());
                return (IReadOnlyList<GoalCompletionUpdate>)Array.Empty<GoalCompletionUpdate>();
            });
        _single = new LogHabitCommandHandler(new LogHabitRepositories(_habits, logs,
                new GenericRepository<User>(_context), new GenericRepository<SentReminder>(_context)),
            new LogHabitServices(_dates, _streaks, _gamification, _goals, mediator, payGate),
            _work, _cache, NullLogger<LogHabitCommandHandler>.Instance);
        _bulk = new BulkLogHabitsCommandHandler(_habits, logs, new BulkLogServices(_dates, _streaks, _gamification),
            _goals, _work, _cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);
        mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => _single.Handle(call.Arg<LogHabitCommand>(), call.Arg<CancellationToken>()));
        _logTool = new LogHabitTool(mediator, _habits, _dates);
        _unlogTool = new UnlogHabitTool(_habits, _dates, _work, payGate, _streaks, _goals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Log_AllChildrenRequired_ParentSlipIsIdempotent(bool astra)
    {
        var (parent, first, second) = await SeedAsync();
        await LogAsync(first, astra);
        ActiveSlips(parent).Should().BeEmpty();
        await LogAsync(second, astra);
        ActiveSlips(parent).Should().ContainSingle().Which.LoggedOnLocalDate.Should().Be(Today);
        await _bulk.Handle(new BulkLogHabitsCommand(_userId, [new(first.Id), new(second.Id)]), default);
        ActiveSlips(parent).Should().ContainSingle();
        await _gamification.Received(1).ProcessHabitLogged(_userId, parent.Id, Arg.Any<CancellationToken>());
        _context.ChangeTracker.Clear();
        (await _context.HabitLogs.Where(log => log.HabitId == parent.Id).ToListAsync())
            .Should().ContainSingle().Which.IsSlip.Should().BeTrue();
    }

    [Fact]
    public async Task BulkLog_AllChildrenTogether_LogsParentOnce()
    {
        var (parent, first, second) = await SeedAsync();
        var command = new BulkLogHabitsCommand(_userId, [new(first.Id), new(second.Id)]);
        var result = await _bulk.Handle(command, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().OnlyContain(item => item.Status == BulkItemStatus.Success);
        ActiveSlips(parent).Should().ContainSingle();
        await _bulk.Handle(command, default);
        ActiveSlips(parent).Should().ContainSingle();
        await _gamification.Received(1).ProcessHabitsLogged(_userId,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(parent.Id)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlog_ChildBreaksSet_RemovesParentSlip(bool astra)
    {
        var (parent, first, second) = await SeedAsync();
        _context.HabitLogs.Add(first.Log(Today).Value);
        _context.HabitLogs.Add(second.Log(Today).Value);
        _context.HabitLogs.Add(parent.Log(Today).Value);
        await _context.SaveChangesAsync();
        if (astra)
            (await _unlogTool.ExecuteAsync(Args(first), _userId, default)).Success.Should().BeTrue();
        else
            (await _single.Handle(new LogHabitCommand(_userId, first.Id), default)).IsSuccess.Should().BeTrue();
        ActiveSlips(first).Should().BeEmpty();
        ActiveSlips(parent).Should().BeEmpty();
        ActiveSlips(second).Should().ContainSingle();
        _context.ChangeTracker.Clear();
        (await _context.HabitLogs.Where(log => log.HabitId == parent.Id).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Log_GoodParent_RemainsUnloggedUntilConfirmation()
    {
        var (parent, first, second) = await SeedAsync(bad: false);
        await LogAsync(first, false);
        await LogAsync(second, false);
        parent.Logs.Should().BeEmpty();
        await LogAsync(parent, false);
        parent.Logs.Should().ContainSingle();
    }

    [Fact]
    public async Task Log_SlipsOnDifferentDates_DoNotCompleteSet()
    {
        var (parent, first, second) = await SeedAsync();
        _context.HabitLogs.Add(first.Log(Today.AddDays(-1)).Value);
        await _context.SaveChangesAsync();
        await LogAsync(second, false);
        ActiveSlips(parent).Should().BeEmpty();
    }

    [Fact]
    public async Task Log_DeletedChildAndGoodChild_DoNotBlockBadParent()
    {
        var (parent, first, second) = await SeedAsync();
        second.SoftDelete();
        _context.Habits.Add(CreateHabit("Good child", false, parent.Id));
        await _context.SaveChangesAsync();
        await LogAsync(first, false);
        ActiveSlips(parent).Should().ContainSingle();
    }

    [Fact]
    public async Task Unlog_AnotherSlipRemainsOnChild_PreservesParent()
    {
        var (parent, first, second) = await SeedAsync();
        _context.HabitLogs.Add(first.Log(Today).Value);
        _context.HabitLogs.Add(first.Log(Today).Value);
        _context.HabitLogs.Add(second.Log(Today).Value);
        _context.HabitLogs.Add(parent.Log(Today).Value);
        await _context.SaveChangesAsync();
        (await _unlogTool.ExecuteAsync(Args(first), _userId, default)).Success.Should().BeTrue();
        ActiveSlips(parent).Should().ContainSingle();
    }

    private async Task LogAsync(Habit habit, bool astra)
    {
        if (astra)
            (await _logTool.ExecuteAsync(Args(habit), _userId, default)).Success.Should().BeTrue();
        else
            (await _single.Handle(new LogHabitCommand(_userId, habit.Id, ToggleExisting: false), default)).IsSuccess.Should().BeTrue();
    }

    private async Task<(Habit Parent, Habit First, Habit Second)> SeedAsync(bool bad = true)
    {
        var parent = CreateHabit("Parent", bad);
        var first = CreateHabit("First child", bad, parent.Id);
        var second = CreateHabit("Second child", bad, parent.Id);
        _context.Habits.AddRange(parent, first, second);
        await _context.SaveChangesAsync();
        return (parent, first, second);
    }

    private Habit CreateHabit(string title, bool bad, Guid? parentId = null) => Habit.Create(new HabitCreateParams(
        HabitSchedule.Recurring, _userId, title, FrequencyUnit.Day, 1, Today,
        IsBadHabit: bad, ParentHabitId: parentId)).Value;

    private static IEnumerable<HabitLog> ActiveSlips(Habit habit) =>
        habit.Logs.Where(log => log.Date == Today && log.IsSlip && log.Value > 0 && !log.IsDeleted);

    private static JsonElement Args(Habit habit) => JsonSerializer.SerializeToElement(new { habit_id = habit.Id });

    private void PassThrough<T>() => _work.ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<Result<T>>>>(), Arg.Any<CancellationToken>())
        .Returns(call => call.Arg<Func<CancellationToken, Task<Result<T>>>>()(call.Arg<CancellationToken>()));

    public async ValueTask DisposeAsync()
    {
        _cache.Dispose();
        await _context.DisposeAsync();
    }
}
