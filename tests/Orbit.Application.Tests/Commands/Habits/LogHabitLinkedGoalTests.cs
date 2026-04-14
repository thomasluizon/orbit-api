using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

/// <summary>
/// Tests for LogHabitCommandHandler covering linked goal updates,
/// referral completion, and gamification result propagation.
/// </summary>
public class LogHabitLinkedGoalTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IStreakFreezeEarnService _streakFreezeEarnService = Substitute.For<IStreakFreezeEarnService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly MediatR.IMediator _mediator = Substitute.For<MediatR.IMediator>();
    private readonly LogHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public LogHabitLinkedGoalTests()
    {
        var repos = new LogHabitRepositories(_habitRepo, _habitLogRepo, _goalRepo, _userRepo);
        var services = new LogHabitServices(_userDateService, _userStreakService, _gamificationService, _streakFreezeEarnService, _mediator);
        _handler = new LogHabitCommandHandler(
            repos, services, _unitOfWork, _cache, Substitute.For<ILogger<LogHabitCommandHandler>>());

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _userStreakService.RecalculateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(5, 5, Today));
        _streakFreezeEarnService.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new StreakFreezeEarnOutcome(0, 0, 0, 0));

        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    [Fact]
    public async Task Handle_WithLinkedStandardGoal_UpdatesGoalProgress()
    {
        var goal = Goal.Create(UserId, "Exercise 10 times", 10, "sessions").Value;
        goal.UpdateProgress(3);

        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Exercise", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        habit.AddGoal(goal);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedGoalUpdates.Should().NotBeNull();
        result.Value.LinkedGoalUpdates!.Count.Should().Be(1);
        result.Value.LinkedGoalUpdates[0].GoalId.Should().Be(goal.Id);
        result.Value.LinkedGoalUpdates[0].NewProgress.Should().Be(4);
    }

    [Fact]
    public async Task Handle_WithLinkedStreakGoal_SyncsStreakProgress()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "7-day streak", 7, "days", Type: GoalType.Streak)).Value;

        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Meditate", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        habit.AddGoal(goal);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedGoalUpdates.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_NoLinkedGoals_LinkedGoalUpdatesIsNull()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Solo", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedGoalUpdates.Should().BeNull();
    }

    [Fact]
    public async Task Handle_GamificationReturnsXp_IncludesInResponse()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _gamificationService.ProcessHabitLogged(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new HabitLogGamificationResult(15, new List<string> { "liftoff" }));

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.XpEarned.Should().Be(15);
        result.Value.NewAchievementIds.Should().Contain("liftoff");
    }

    [Fact]
    public async Task Handle_ReferralCompletionFails_DoesNotBreakLog()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _mediator.Send(Arg.Any<MediatR.IRequest<Orbit.Domain.Common.Result>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Referral service down"));

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_StreakRecalculateReturnsNull_UsesZeroStreak()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _userStreakService.RecalculateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UserStreakState?)null);

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(0);
    }
}
