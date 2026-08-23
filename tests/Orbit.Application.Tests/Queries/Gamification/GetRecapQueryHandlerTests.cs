using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetRecapQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GetRecapQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly DateTo = new(2026, 6, 20);
    private static readonly DateOnly DateFrom = DateTo.AddDays(-6);
    private const string ReferralCode = "ABCD2345";

    public GetRecapQueryHandlerTests()
    {
        var frontendSettings = Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" });
        _handler = new GetRecapQueryHandler(
            _habitRepo,
            _goalRepo,
            _userRepo,
            _userStreakService,
            frontendSettings,
            _mediator,
            _cache);

        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ReferralCode));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(CreateUser("UTC", new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
    }

    private void StubHabits(params Habit[] habits)
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.ToList().AsReadOnly());
    }

    private static Habit CreateLoggedDailyHabit()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Read", FrequencyUnit.Day, 1, DueDate: DateFrom)).Value;
        habit.Log(DateFrom);
        habit.Log(DateFrom.AddDays(1));
        habit.Log(DateFrom.AddDays(2));
        return habit;
    }

    private static User CreateUser(string timeZone, DateTime createdAtUtc, bool pro = false)
    {
        var user = User.Create("Recap User", $"recap-{Guid.NewGuid():N}@example.com").Value;
        user.SetTimeZone(timeZone).IsSuccess.Should().BeTrue();
        if (pro)
            user.GrantLifetimePro();
        typeof(User).GetProperty(nameof(User.CreatedAtUtc))!.SetValue(user, createdAtUtc);
        return user;
    }

    private static Goal CreateCompletedGoal(DateTime completedAtUtc, bool deleted = false, bool reopened = false)
    {
        var goal = Goal.Create(UserId, $"Goal {Guid.NewGuid():N}", 1, "times").Value;
        goal.MarkCompleted().IsSuccess.Should().BeTrue();
        typeof(Goal).GetProperty(nameof(Goal.CompletedAtUtc))!.SetValue(goal, completedAtUtc);
        if (reopened)
            goal.Reactivate().IsSuccess.Should().BeTrue();
        if (deleted)
            goal.SoftDelete();
        return goal;
    }

    private void StubGoalCount(params Goal[] goals)
    {
        _goalRepo.CountAsync(
                Arg.Any<Expression<Func<Goal, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.ArgAt<Expression<Func<Goal, bool>>>(0).Compile();
                return goals.Count(predicate);
            });
    }

    [Fact]
    public async Task Handle_ComputesMetrics_MatchingCalculator()
    {
        var habit = CreateLoggedDailyHabit();
        StubHabits(habit);
        _userStreakService.RecalculateAsync(UserId, awardFreezeIfEligible: false, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(7, 20, DateTo));

        var query = new GetRecapQuery(UserId, DateFrom, DateTo, "week");

        var result = await _handler.Handle(query, CancellationToken.None);

        var expected = RetrospectiveMetricsCalculator.Compute(
            new List<Habit> { habit }, DateFrom, DateTo, 7, 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Period.Should().Be("week");
        result.Value.Metrics.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Handle_TwoLogs_ReturnsExactlyTwoCompletions()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Walk", FrequencyUnit.Day, 1, DueDate: DateFrom)).Value;
        habit.Log(DateFrom);
        habit.Log(DateFrom.AddDays(1));
        StubHabits(habit);

        var result = await _handler.Handle(
            new GetRecapQuery(UserId, DateFrom, DateTo, "week"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Metrics.TotalCompletions.Should().Be(2);
    }

    [Fact]
    public async Task Handle_ShareDeepLink_ContainsReferralCodeAndPeriod()
    {
        StubHabits(CreateLoggedDailyHabit());
        _userStreakService.RecalculateAsync(UserId, awardFreezeIfEligible: false, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(1, 1, DateTo));

        var query = new GetRecapQuery(UserId, DateFrom, DateTo, "month");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ShareDeepLink.Should().Be("https://app.useorbit.org/r/ABCD2345?recap=month");
        result.Value.DateFrom.Should().BeNull();
        result.Value.DateTo.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ClosedMonth_ReturnsBoundsGoalCountAndAddressableShareLink()
    {
        StubHabits(CreateLoggedDailyHabit());
        StubGoalCount(
            CreateCompletedGoal(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateCompletedGoal(new DateTime(2026, 6, 30, 23, 59, 0, DateTimeKind.Utc)),
            CreateCompletedGoal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateCompletedGoal(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc), deleted: true),
            CreateCompletedGoal(new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc), reopened: true));
        _userStreakService.RecalculateAsync(UserId, awardFreezeIfEligible: false, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(3, 8, new DateOnly(2026, 6, 30)));

        var query = new GetRecapQuery(
            UserId,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            "month",
            2026,
            6);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GoalCompletions.Should().Be(2);
        result.Value.DateFrom.Should().Be(new DateOnly(2026, 6, 1));
        result.Value.DateTo.Should().Be(new DateOnly(2026, 6, 30));
        result.Value.ShareDeepLink.Should().Be(
            "https://app.useorbit.org/r/ABCD2345?recap=month&year=2026&month=6");
    }

    [Fact]
    public async Task Handle_ClosedMonthWithNoGoalCompletion_ReturnsZero()
    {
        StubHabits();
        StubGoalCount(CreateCompletedGoal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(
            new GetRecapQuery(
                UserId,
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                "month",
                2026,
                6),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GoalCompletions.Should().Be(0);
    }

    [Fact]
    public async Task Handle_GoalCompletionBoundary_UsesUserTimezone()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(CreateUser(
                "America/Sao_Paulo",
                new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
        StubHabits();
        StubGoalCount(
            CreateCompletedGoal(new DateTime(2026, 7, 1, 2, 30, 0, DateTimeKind.Utc)),
            CreateCompletedGoal(new DateTime(2026, 7, 1, 3, 30, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(
            new GetRecapQuery(
                UserId,
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                "month",
                2026,
                6),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GoalCompletions.Should().Be(1);
    }

    [Fact]
    public async Task Handle_MonthBeforeLocalizedAccountMonth_ReturnsNamedFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(CreateUser(
                "America/Sao_Paulo",
                new DateTime(2026, 7, 1, 3, 30, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(
            new GetRecapQuery(
                UserId,
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                "month",
                2026,
                6),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.RecapMonthBeforeAccount);
        await _mediator.DidNotReceive()
            .Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_ClosedMonth_IsAvailableRegardlessOfPlan(bool pro)
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(CreateUser("UTC", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), pro));
        StubHabits();

        var result = await _handler.Handle(
            new GetRecapQuery(
                UserId,
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                "month",
                2026,
                6),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ClosedMonthTwice_ReturnsCachedIdenticalResponse()
    {
        StubHabits(CreateLoggedDailyHabit());
        StubGoalCount(CreateCompletedGoal(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc)));
        var query = new GetRecapQuery(
            UserId,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            "month",
            2026,
            6);

        var first = await _handler.Handle(query, CancellationToken.None);
        var second = await _handler.Handle(query, CancellationToken.None);

        second.Value.Should().BeEquivalentTo(first.Value);
        await _habitRepo.Received(1).FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>());
        await _goalRepo.Received(1).CountAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyPeriod_ReturnsZeroedMetrics_NotFailure()
    {
        StubHabits();
        _userStreakService.RecalculateAsync(UserId, awardFreezeIfEligible: false, Arg.Any<CancellationToken>())
            .Returns((UserStreakState?)null);

        var query = new GetRecapQuery(UserId, DateFrom, DateTo, "week");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Metrics.TotalCompletions.Should().Be(0);
        result.Value.Metrics.CompletionRate.Should().Be(0);
        result.Value.Metrics.CurrentStreak.Should().Be(0);
        result.Value.Metrics.TopHabits.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ReferralCommandFails_PropagatesFailure()
    {
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>(ErrorMessages.UserNotFound));

        var query = new GetRecapQuery(UserId, DateFrom, DateTo, "week");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}
