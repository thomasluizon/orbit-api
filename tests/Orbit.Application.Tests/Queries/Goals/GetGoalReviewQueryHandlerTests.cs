using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
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
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGoalReviewService _reviewService = Substitute.For<IGoalReviewService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IGoalProgressReadSyncer _goalProgressReadSyncer = Substitute.For<IGoalProgressReadSyncer>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GetGoalReviewQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetGoalReviewQueryHandlerTests()
    {
        _handler = new GetGoalReviewQueryHandler(
            _goalRepo,
            _payGate,
            _unitOfWork,
            _reviewService,
            _userDateService,
            _goalProgressReadSyncer,
            _cache);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _goalProgressReadSyncer.ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _payGate.TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    private static Goal CreateTestGoal()
    {
        return Goal.Create(UserId, "Active Goal", 100, "pages").Value;
    }

    [Fact]
    public async Task Handle_GeneratesNewReview_WhenNotCached()
    {
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
        await _payGate.Received(1).TryConsumeAiMessage(
            UserId, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DecimalProgress_UsesInvariantCultureInReviewContext()
    {
        var goal = Goal.Create(UserId, "Run", 10.5m, "miles").Value;
        goal.UpdateProgress(3.5m);
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal }.AsReadOnly());
        string? capturedContext = null;
        _reviewService.GenerateReviewAsync(
            Arg.Do<string>(context => capturedContext = context), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success("Review content"));
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var result = await _handler.Handle(new GetGoalReviewQuery(UserId, "en"), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            capturedContext.Should().Contain("3.5/10.5 miles");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task Handle_Deadline_UsesInvariantCalendarInReviewContext()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Run", 10, "miles", Deadline: new DateOnly(2026, 12, 31))).Value;
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal }.AsReadOnly());
        string? capturedContext = null;
        _reviewService.GenerateReviewAsync(
            Arg.Do<string>(context => capturedContext = context), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success("Review content"));
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var result = await _handler.Handle(new GetGoalReviewQuery(UserId, "en"), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            capturedContext.Should().Contain("Deadline: 2026-12-31");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task Handle_RefreshesStreakGoalValue_BeforeBuildingContext()
    {
        var streakGoal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", 7, "days", Type: GoalType.Streak)).Value;
        var badHabit = Habit.Create(new HabitCreateParams(
            UserId, "Doom scrolling", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today.AddDays(-3))).Value;
        badHabit.AddGoal(streakGoal);
        streakGoal.AddHabit(badHabit);

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => new List<Goal> { streakGoal }.AsReadOnly());
        _goalProgressReadSyncer.ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [streakGoal.Id] = 4 });

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
        await _payGate.Received(1).TryConsumeAiMessage(
            UserId, _unitOfWork, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FreeUser_GeneratesReview()
    {
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { CreateTestGoal() }.AsReadOnly());
        _reviewService.GenerateReviewAsync(
            Arg.Any<string>(), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success("Review content"));

        var query = new GetGoalReviewQuery(UserId, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Review.Should().Be("Review content");
    }

    [Fact]
    public async Task Handle_DailyAiLimitReached_DoesNotGenerateReview()
    {
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { CreateTestGoal() }.AsReadOnly());
        _payGate.TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Daily AI limit reached"));

        var result = await _handler.Handle(new GetGoalReviewQuery(UserId, "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _reviewService.DidNotReceive().GenerateReviewAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoActiveGoals_ReturnsFailure()
    {
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal>().AsReadOnly());

        var query = new GetGoalReviewQuery(UserId, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.NoActiveGoals.Message);
        await _payGate.DidNotReceive().TryConsumeAiMessage(
            UserId, _unitOfWork, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReviewServiceFails_ReturnsFailure()
    {
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
