using FluentAssertions;
using MediatR;
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
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly GetRecapQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly DateTo = new(2026, 6, 20);
    private static readonly DateOnly DateFrom = DateTo.AddDays(-6);
    private const string ReferralCode = "ABCD2345";

    public GetRecapQueryHandlerTests()
    {
        var frontendSettings = Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" });
        _handler = new GetRecapQueryHandler(_habitRepo, _userStreakService, frontendSettings, _mediator);

        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ReferralCode));
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
    public async Task Handle_ShareDeepLink_ContainsReferralCodeAndPeriod()
    {
        StubHabits(CreateLoggedDailyHabit());
        _userStreakService.RecalculateAsync(UserId, awardFreezeIfEligible: false, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(1, 1, DateTo));

        var query = new GetRecapQuery(UserId, DateFrom, DateTo, "month");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ShareDeepLink.Should().Be("https://app.useorbit.org/r/ABCD2345?recap=month");
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
