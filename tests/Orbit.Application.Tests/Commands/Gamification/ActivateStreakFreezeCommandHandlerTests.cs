using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Gamification;

public class ActivateStreakFreezeCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ActivateStreakFreezeCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 15);

    public ActivateStreakFreezeCommandHandlerTests()
    {
        _handler = new ActivateStreakFreezeCommandHandler(
            _userRepo, _streakFreezeRepo, _habitLogRepo, _habitRepo, _userStreakService, _userDateService, _unitOfWork);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static User CreateUserWithEarnedBalance(int balance = 1, int streak = 7)
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetStreakState(streak, streak, Today.AddDays(-1));
        // Run domain earn; anchor was 0, so streak/7 freezes are added, capped to hold cap.
        user.TryEarnStreakFreezes();
        // If needed, trim balance by consuming any extras so the test starts at the desired balance.
        while (user.StreakFreezeBalance > balance)
        {
            user.ConsumeStreakFreeze();
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

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _userStreakService.RecalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(0, 0, null));

        var command = new ActivateStreakFreezeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No active streak");
    }

    [Fact]
    public async Task Handle_AlreadyFrozenToday_ReturnsFailure()
    {
        var user = CreateUserWithEarnedBalance();

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _userStreakService.RecalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(7, 7, Today.AddDays(-1)));

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
    public async Task Handle_NoEarnedFreezes_ReturnsNoStreakFreezesEarnedFailure()
    {
        // Streak > 0 but user never reached 7 days, so balance is 0.
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStreakState(5, 5, Today.AddDays(-1));

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _userStreakService.RecalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(5, 5, Today.AddDays(-1)));

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var command = new ActivateStreakFreezeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("NO_STREAK_FREEZES_EARNED");
    }

    [Fact]
    public async Task Handle_MonthlyLimitReached_ReturnsFailure()
    {
        var user = CreateUserWithEarnedBalance(balance: 3, streak: 21);

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _userStreakService.RecalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(21, 21, Today.AddDays(-1)));

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        // First call: no freeze today. Second call: monthly usage already 3.
        var monthFreezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, new DateOnly(Today.Year, Today.Month, 1)),
            StreakFreeze.Create(UserId, new DateOnly(Today.Year, Today.Month, 5)),
            StreakFreeze.Create(UserId, new DateOnly(Today.Year, Today.Month, 10))
        };

        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(
                new List<StreakFreeze>().AsReadOnly(),
                monthFreezes.AsReadOnly());

        var command = new ActivateStreakFreezeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("STREAK_FREEZE_MONTHLY_LIMIT_REACHED");
    }

    [Fact]
    public async Task Handle_ValidFreeze_DecrementsBalanceAndInsertsRow()
    {
        var user = CreateUserWithEarnedBalance(balance: 2, streak: 14);
        user.StreakFreezeBalance.Should().Be(2);

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _userStreakService.RecalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(
                new UserStreakState(14, 14, Today.AddDays(-1)),
                new UserStreakState(14, 14, Today));

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

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
        result.Value.FreezesRemainingBalance.Should().Be(1); // was 2, consumed 1
        result.Value.FreezesUsedThisMonth.Should().Be(1);
        result.Value.MaxFreezesPerMonth.Should().Be(3);
        result.Value.MaxFreezesHeld.Should().Be(3);

        await _streakFreezeRepo.Received(1).AddAsync(Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
