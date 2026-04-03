using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Gamification;

public class ActivateStreakFreezeCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ActivateStreakFreezeCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public ActivateStreakFreezeCommandHandlerTests()
    {
        _handler = new ActivateStreakFreezeCommandHandler(
            _userRepo, _streakFreezeRepo, _habitLogRepo, _habitRepo, _userDateService, _unitOfWork);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static User CreateUserWithStreak(int streak = 5)
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.UpdateStreak(Today.AddDays(-1));
        // Build streak by calling UpdateStreak for consecutive days
        for (int i = streak - 1; i >= 1; i--)
        {
            user.UpdateStreak(Today.AddDays(-i));
        }
        return user;
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var command = new ActivateStreakFreezeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task Handle_NoActiveStreak_ReturnsFailure()
    {
        var user = User.Create("Test", "test@example.com").Value;
        // Streak is 0 by default

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var command = new ActivateStreakFreezeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No active streak");
    }

    [Fact]
    public async Task Handle_AlreadyFrozenToday_ReturnsFailure()
    {
        var user = CreateUserWithStreak();

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        // User has no habits (no need to check logs)
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        // Already has a freeze today
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(
                new List<StreakFreeze> { StreakFreeze.Create(UserId, Today) }.AsReadOnly(),
                new List<StreakFreeze>().AsReadOnly());

        var command = new ActivateStreakFreezeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already used");
    }

    [Fact]
    public async Task Handle_MaxFreezesReached_ReturnsFailure()
    {
        var user = CreateUserWithStreak();

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        // No freeze today but 3 recent freezes (at max)
        var recentFreezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today.AddDays(-1)),
            StreakFreeze.Create(UserId, Today.AddDays(-5)),
            StreakFreeze.Create(UserId, Today.AddDays(-10))
        };

        // First call: check existing freeze today (empty)
        // Second call: check rolling window (3 freezes)
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(
                new List<StreakFreeze>().AsReadOnly(),
                recentFreezes.AsReadOnly());

        var command = new ActivateStreakFreezeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No streak freeze available");
    }

    [Fact]
    public async Task Handle_ValidFreeze_ReturnsSuccess()
    {
        var user = CreateUserWithStreak();

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        // No existing freeze today, no recent freezes
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(
                new List<StreakFreeze>().AsReadOnly(),
                new List<StreakFreeze>().AsReadOnly());

        var command = new ActivateStreakFreezeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FrozenDate.Should().Be(Today);
        result.Value.FreezesRemainingThisMonth.Should().Be(2);

        await _streakFreezeRepo.Received(1).AddAsync(Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
