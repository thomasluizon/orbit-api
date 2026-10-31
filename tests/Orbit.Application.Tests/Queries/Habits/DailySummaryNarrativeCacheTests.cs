using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Models;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Habits;

public sealed class DailySummaryNarrativeCacheTests : IDisposable
{
    private readonly Guid userId = Guid.NewGuid();
    private readonly DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
    private readonly MemoryCache cache = new(new MemoryCacheOptions());
    private readonly ISummaryService summaries = Substitute.For<ISummaryService>();
    private readonly GetDailySummaryQueryHandler handler;
    private readonly LogHabitCommandHandler logHandler;
    private readonly List<Habit> habits = [];

    public DailySummaryNarrativeCacheTests()
    {
        var repo = Substitute.For<IGenericRepository<Habit>>();
        repo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns(habits);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(User.Create("Person", "person@example.com").Value);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        var gate = Substitute.For<IPayGateService>();
        gate.CanUseDailySummary(userId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var schedule = DailySummaryScheduleFixture.Project(habits, today);
            return Result.Success(new PaginatedResponse<HabitScheduleItem>(schedule, 1, 100, schedule.Count, 1));
        });
        var logs = Substitute.For<IHabitSummaryLogReader>();
        logs.ReadAsync(Arg.Any<IReadOnlyCollection<HabitSummaryLogWindow>>(), Arg.Any<CancellationToken>())
            .Returns(_ => habits.SelectMany(habit => habit.Logs).Select(log => new HabitSummaryLogFact(log.HabitId, log.Date, log.Value)).ToList());
        repo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.FirstOrDefault(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()));
        var unit = Substitute.For<IUnitOfWork>();
        unit.PassThroughTransactions<Result<LogHabitCommandHandler.PersistedLog>>();
        var streaks = Substitute.For<IUserStreakService>();
        streaks.RecalculateAsync(userId, cancellationToken: Arg.Any<CancellationToken>()).Returns(new UserStreakState(1, 1, today));
        var goals = Substitute.For<IGoalCompletionService>();
        goals.SyncDerivedGoalsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<GoalCompletionUpdate>());
        logHandler = new(new(repo, Substitute.For<IGenericRepository<HabitLog>>(), users, Substitute.For<IGenericRepository<SentReminder>>(),
            Substitute.For<IGenericRepository<Notification>>()),
            new(dates, streaks, Substitute.For<IGamificationService>(), goals, mediator, gate), unit, cache,
            NullLogger<LogHabitCommandHandler>.Instance);
        handler = new(repo, users, Substitute.For<IGenericRepository<HabitLog>>(), logs, gate, summaries, cache, dates, mediator);
    }

    [Fact]
    public async Task Cache_RefreshesWhenChosenNextStepChanges()
    {
        var first = Add("Psyllium 5g", new TimeOnly(10, 30));
        Add("Lunch", new TimeOnly(12, 0));
        summaries.GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("The day is open, starting with psyllium.", string.Empty)));
        (await Read()).Value.FromCache.Should().BeFalse();
        (await Read()).Value.FromCache.Should().BeTrue();
        first.Log(today).IsSuccess.Should().BeTrue();
        var result = await Read();
        result.Value.FromCache.Should().BeFalse();
        await summaries.Received(2).GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cache_KeepsNarrativeWhenAnotherHabitIsLogged()
    {
        Add("Psyllium 5g", new TimeOnly(10, 30));
        var later = Add("Lunch", new TimeOnly(12, 0));
        summaries.GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Meals remain open, starting with psyllium.", string.Empty)));
        await Read();
        (await logHandler.Handle(new(userId, later.Id, today), default)).IsSuccess.Should().BeTrue();
        (await Read()).Value.FromCache.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cache_NeverStoresFailedOrEmptyNarrative(bool empty)
    {
        summaries.GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(), Arg.Any<CancellationToken>())
            .Returns(empty ? Result.Success(new DailySummaryContent("  ", string.Empty))
                : Result.Failure<DailySummaryContent>(ErrorMessages.AiSummaryUnavailable));
        (await Read()).IsFailure.Should().BeTrue();
        (await Read()).IsFailure.Should().BeTrue();
        await summaries.Received(2).GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cache_ExplicitEditInvalidationClearsSelectedStepVariant()
    {
        Add("Psyllium 5g", new TimeOnly(10, 30));
        summaries.GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Your day is open, starting with psyllium.", string.Empty)));
        await Read();
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, userId, today);
        (await Read()).Value.FromCache.Should().BeFalse();
        await summaries.Received(2).GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(), Arg.Any<CancellationToken>());
    }

    private Habit Add(string title, TimeOnly time)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId, title,
            FrequencyUnit.Day, 1, DueDate: today, DueTime: time, Position: habits.Count)).Value;
        habits.Add(habit);
        return habit;
    }

    private Task<Result<DailySummaryResponse>> Read() => handler.Handle(new(userId, today, today, "en"), default);

    public void Dispose() => cache.Dispose();
}
