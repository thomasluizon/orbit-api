using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Goals.Queries;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Goals;

public class GetGoalReviewQueryHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGoalReviewService _reviewService = Substitute.For<IGoalReviewService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GetGoalReviewQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetGoalReviewQueryHandlerTests()
    {
        _handler = new GetGoalReviewQueryHandler(
            _goalRepo, _payGate, _reviewService, _userDateService, _cache);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Goal CreateTestGoal()
    {
        return Goal.Create(UserId, "Active Goal", 100, "pages").Value;
    }

    private static void SetCreatedAtUtc(Habit habit, DateOnly localDate)
    {
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Handle_GeneratesNewReview_WhenNotCached()
    {
        _payGate.CanAccessGoals(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var goal = CreateTestGoal();
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal }.AsReadOnly());

        _reviewService.GenerateReviewAsync(
            Arg.Any<string>(), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success("Review content"));

        var query = new GetGoalReviewQuery(UserId, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Review.Should().Be("Review content");
        result.Value.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_RefreshesStreakGoalValue_BeforeBuildingContext()
    {
        _payGate.CanAccessGoals(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var streakGoal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", 7, "days", Type: GoalType.Streak)).Value;
        var badHabit = Habit.Create(new HabitCreateParams(
            UserId, "Doom scrolling", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: Today)).Value;
        SetCreatedAtUtc(badHabit, Today.AddDays(-3));
        badHabit.AddGoal(streakGoal);
        streakGoal.AddHabit(badHabit);

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => new List<Goal> { streakGoal }.AsReadOnly());

        string? capturedContext = null;
        _reviewService.GenerateReviewAsync(
            Arg.Do<string>(ctx => capturedContext = ctx), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success("Review content"));

        var result = await _handler.Handle(new GetGoalReviewQuery(UserId, "en"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        capturedContext.Should().NotBeNull();
        capturedContext.Should().Contain("4/7 days");
        streakGoal.Status.Should().Be(GoalStatus.Active);
    }

    [Fact]
    public async Task Handle_ReturnsCachedReview_WhenCached()
    {
        _payGate.CanAccessGoals(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var goal = CreateTestGoal();
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal }.AsReadOnly());

        _reviewService.GenerateReviewAsync(
            Arg.Any<string>(), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success("Review content"));

        var query = new GetGoalReviewQuery(UserId, "en");

        await _handler.Handle(query, CancellationToken.None);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_PayGateFails_ReturnsFailure()
    {
        _payGate.CanAccessGoals(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Goals are a Pro feature"));

        var query = new GetGoalReviewQuery(UserId, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NoActiveGoals_ReturnsFailure()
    {
        _payGate.CanAccessGoals(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal>().AsReadOnly());

        var query = new GetGoalReviewQuery(UserId, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No active goals found");
    }

    [Fact]
    public async Task Handle_ReviewServiceFails_ReturnsFailure()
    {
        _payGate.CanAccessGoals(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var goal = CreateTestGoal();
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal }.AsReadOnly());

        _reviewService.GenerateReviewAsync(
            Arg.Any<string>(), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("AI service error"));

        var query = new GetGoalReviewQuery(UserId, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("AI service error");
    }
}
