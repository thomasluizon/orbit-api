using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Profile;

public class ApplyOnboardingCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IAppConfigService _appConfig = Substitute.For<IAppConfigService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 7, 5);

    public ApplyOnboardingCommandHandlerTests()
    {
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _appConfig.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits);
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var operation = call.ArgAt<Func<CancellationToken, Task>>(0);
                var ct = call.ArgAt<CancellationToken>(1);
                return operation(ct);
            });
    }

    private ApplyOnboardingCommandHandler CreateHandler() => new(
        _userRepo, _habitRepo, _goalRepo, _payGate, _userDateService, _appConfig, _unitOfWork, _cache);

    private void SetupUser(User user)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    private static User CreateProUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Free User", "free@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private static ApplyHabitInput Habit(string title) =>
        new(title, null, null, FrequencyUnit.Day, 1);

    [Fact]
    public async Task Apply_HappyPath_CreatesEverythingAndCompletesOnboarding()
    {
        var user = CreateProUser();
        SetupUser(user);

        var command = new ApplyOnboardingCommand(
            UserId,
            [Habit("Drink water"), Habit("Read")],
            new ApplyLogInput(0, Today),
            new ApplyGoalInput("Run 100km", null, 100, "km"),
            WeekStartDay: 0,
            ColorScheme: "blue");

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Applied.Should().BeTrue();
        result.Value.CreatedHabitCount.Should().Be(2);
        result.Value.CreatedGoal.Should().BeTrue();
        result.Value.LoggedFirstHabit.Should().BeTrue();
        user.HasCompletedOnboarding.Should().BeTrue();
        user.WeekStartDay.Should().Be(0);
        user.ColorScheme.Should().Be("blue");
        await _habitRepo.Received(2).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _goalRepo.Received(1).AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_AlreadyOnboarded_IsNoOp()
    {
        var user = CreateProUser();
        user.CompleteOnboarding();
        SetupUser(user);

        var command = new ApplyOnboardingCommand(
            UserId, [Habit("Drink water")], null, null, null, null);

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Applied.Should().BeFalse();
        result.Value.CreatedHabitCount.Should().Be(0);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_UserNotFound_ReturnsFailure()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var command = new ApplyOnboardingCommand(
            UserId, [Habit("Drink water")], null, null, null, null);

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_HabitCreationFails_RollsBackWithoutSaving()
    {
        var user = CreateProUser();
        SetupUser(user);

        var command = new ApplyOnboardingCommand(
            UserId, [Habit("Valid"), Habit("   ")], null, null, null, null);

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        user.HasCompletedOnboarding.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_FreeUserOverCap_TrimsToAllowance()
    {
        var user = CreateFreeUser();
        SetupUser(user);
        _habitRepo.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits - 1);

        var command = new ApplyOnboardingCommand(
            UserId, [Habit("One"), Habit("Two"), Habit("Three")], null, null, null, null);

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Applied.Should().BeTrue();
        result.Value.CreatedHabitCount.Should().Be(1);
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        user.HasCompletedOnboarding.Should().BeTrue();
    }

    [Fact]
    public async Task Apply_GoalGateFails_SkipsGoalButStillApplies()
    {
        var user = CreateFreeUser();
        SetupUser(user);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.PayGateFailure("Goals are a Pro feature. Upgrade to unlock!")));

        var command = new ApplyOnboardingCommand(
            UserId, [Habit("Drink water")], null,
            new ApplyGoalInput("Run 100km", null, 100, "km"), null, null);

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Applied.Should().BeTrue();
        result.Value.CreatedGoal.Should().BeFalse();
        result.Value.CreatedHabitCount.Should().Be(1);
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
        user.HasCompletedOnboarding.Should().BeTrue();
    }
}
