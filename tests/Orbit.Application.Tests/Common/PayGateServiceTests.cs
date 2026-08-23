using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Common;

[Collection("ProcessEnvironment")]
public class PayGateServiceTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IAppConfigService _appConfig = Substitute.For<IAppConfigService>();
    private readonly PayGateService _sut;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly ReactivationToday = new(2026, 8, 5);

    public PayGateServiceTests()
    {
        _sut = new PayGateService(_habitRepo, _userRepo, _appConfig);

        _appConfig.GetAsync(
                AppConfigKeys.FreeMaxHabits,
                AppConstants.DefaultFreeMaxHabits,
                Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits);
        _appConfig.GetAsync("SubHabitsProOnly", true, Arg.Any<CancellationToken>()).Returns(true);
        _appConfig.GetAsync("FreeAiMessagesPerMonth", 20, Arg.Any<CancellationToken>()).Returns(20);
        _appConfig.GetAsync("ProAiMessagesPerMonth", 500, Arg.Any<CancellationToken>()).Returns(500);
        _appConfig.GetAsync("DailySummaryProOnly", true, Arg.Any<CancellationToken>()).Returns(true);
        _appConfig.GetAsync("RetrospectiveProOnly", true, Arg.Any<CancellationToken>()).Returns(true);
        _appConfig.GetAsync("GoalsProOnly", true, Arg.Any<CancellationToken>()).Returns(true);
    }

    private static User CreateFreeUser()
    {
        var result = User.Create("Test User", "test@example.com");
        return result.Value;
    }

    private static User CreateProUser()
    {
        var user = CreateFreeUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddYears(1), SubscriptionInterval.Monthly);
        return user;
    }

    private static Habit CreateCompletedOneTimeTask(int index)
    {
        var dueDate = new DateOnly(2026, 8, 5);
        var task = Habit.Create(new HabitCreateParams(
            UserId, $"Finished task {index}", null, null, dueDate)).Value;
        task.Log(dueDate).IsSuccess.Should().BeTrue();
        return task;
    }

    private static Habit CreateCompletedRecurringHabit(int index)
    {
        var dueDate = new DateOnly(2026, 8, 5);
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            $"Finished recurring habit {index}",
            FrequencyUnit.Day,
            1,
            dueDate,
            EndDate: dueDate)).Value;
        habit.Log(dueDate).IsSuccess.Should().BeTrue();
        return habit;
    }

    [Fact]
    public async Task CanCreateHabits_ProUserAtLimit_PayGateFailure()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitRepo.CountAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits);

        var result = await _sut.CanCreateHabits(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        result.Error.Should().Be("You've reached the 1000 habit limit.");
    }

    [Fact]
    public async Task CanCreateHabits_FreeUserAt999_AllowsOneMore()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitRepo.CountAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits - 1);

        var result = await _sut.CanCreateHabits(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateHabits_FreeUserWithCompletedTasks_Success()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        var completedTasks = Enumerable.Range(1, 10).Select(CreateCompletedOneTimeTask).ToList();
        _habitRepo.CountAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => completedTasks.Count(
                call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()));

        var result = await _sut.CanCreateHabits(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateHabits_FreeUserWithCompletedRecurringHabits_Success()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        var completedHabits = Enumerable.Range(1, 10).Select(CreateCompletedRecurringHabit).ToList();
        _habitRepo.CountAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => completedHabits.Count(
                call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()));

        var result = await _sut.CanCreateHabits(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateHabits_FreeUserAtLimit_PayGateFailureWithoutUpsell()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.CountAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits);

        var result = await _sut.CanCreateHabits(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        result.Error.Should().Be("You've reached the 1000 habit limit.");
    }

    [Fact]
    public async Task CanCreateHabits_FreeUserAt998_CreatingFiveFails()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitRepo.CountAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits - 2);

        var result = await _sut.CanCreateHabits(UserId, 5);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        result.Error.Should().Be("You've reached the 1000 habit limit.");
    }

    [Fact]
    public async Task UnlogCompletedTask_FreeUserAtLimit_RejectsWithoutChangingState()
    {
        ConfigureFreeUserAtHabitCap();
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Finished task", null, null, ReactivationToday)).Value;
        habit.Log(ReactivationToday).IsSuccess.Should().BeTrue();

        var result = await HabitReactivationAllowance.ExecuteAsync(
            UserId,
            HabitReactivationAllowance.IsRequiredForUnlog(habit),
            _sut,
            () => habit.Unlog(ReactivationToday),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        habit.IsCompleted.Should().BeTrue();
        habit.Logs.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EndDateReactivation_FreeUserAtLimit_RejectsWithoutChangingState(bool clearEndDate)
    {
        ConfigureFreeUserAtHabitCap();
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Finished recurring habit",
            FrequencyUnit.Day,
            1,
            ReactivationToday,
            EndDate: ReactivationToday)).Value;
        habit.Log(ReactivationToday).IsSuccess.Should().BeTrue();
        var originalDueDate = habit.DueDate;
        DateOnly? endDate = clearEndDate ? null : ReactivationToday.AddDays(7);

        var result = await HabitReactivationAllowance.ExecuteAsync(
            UserId,
            HabitReactivationAllowance.IsRequiredForEndDateChange(
                habit,
                FrequencyUnit.Day,
                dueDate: null,
                endDate,
                clearEndDate),
            _sut,
            () => habit.Update(new HabitUpdateParams(
                "Changed title",
                null,
                FrequencyUnit.Day,
                1,
                null,
                false,
                null,
                EndDate: endDate,
                ClearEndDate: clearEndDate)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        habit.Title.Should().Be("Finished recurring habit");
        habit.IsCompleted.Should().BeTrue();
        habit.DueDate.Should().Be(originalDueDate);
        habit.EndDate.Should().Be(ReactivationToday);
    }

    [Fact]
    public async Task CanCreateHabits_UserNotFound_Failure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.CanCreateHabits(UserId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    private void ConfigureFreeUserAtHabitCap()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitRepo.CountAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits);
    }

    [Fact]
    public async Task CanCreateSubHabits_ProUser_Success()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanCreateSubHabits(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateSubHabits_FreeUser_PayGateFailure()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanCreateSubHabits(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanCreateSubHabits_ConfigDisabled_FreeUserAllowed()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _appConfig.GetAsync("SubHabitsProOnly", true, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CanCreateSubHabits(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanSendAiMessage_UnderLimit_Success()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanSendAiMessage(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanSendAiMessage_AtLimit_PayGateFailure()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (int i = 0; i < 20; i++)
            user.IncrementAiMessageCount();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanSendAiMessage(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanSendAiMessage_ProductionSmokeAccount_OverLimit_Bypasses()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (int i = 0; i < 20; i++)
            user.IncrementAiMessageCount();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        await WithEnvironment("Production", user.Email, async () =>
        {
            var result = await _sut.CanSendAiMessage(UserId);
            result.IsSuccess.Should().BeTrue();
        });
    }

    [Fact]
    public async Task CanSendAiMessage_ProductionNonSmokeEmail_OverLimit_StillBlocked()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (int i = 0; i < 20; i++)
            user.IncrementAiMessageCount();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        await WithEnvironment("Production", "not-the-smoke@example.com", async () =>
        {
            var result = await _sut.CanSendAiMessage(UserId);
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be("PAY_GATE");
        });
    }

    [Fact]
    public async Task CanSendAiMessage_NonProductionSmokeEmail_OverLimit_StillBlocked()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (int i = 0; i < 20; i++)
            user.IncrementAiMessageCount();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        await WithEnvironment("Development", user.Email, async () =>
        {
            var result = await _sut.CanSendAiMessage(UserId);
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be("PAY_GATE");
        });
    }

    [Fact]
    public async Task CanUseDailySummary_ProUser_Success()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseDailySummary(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseDailySummary_FreeUser_PayGateFailure()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseDailySummary(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task GetAiMessageLimit_ProUser_ReturnsProLimit()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var limit = await _sut.GetAiMessageLimit(UserId);

        limit.Should().Be(500);
    }

    [Fact]
    public async Task GetAiMessageLimit_FreeUser_ReturnsFreeLimit()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var limit = await _sut.GetAiMessageLimit(UserId);

        limit.Should().Be(20);
    }

    [Fact]
    public async Task GetAiMessageLimit_UserNotFound_ReturnsDefault20()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var limit = await _sut.GetAiMessageLimit(UserId);

        limit.Should().Be(20);
    }

    [Fact]
    public async Task CanUseRetrospective_AnnualSubscription_Success()
    {
        var user = CreateFreeUser();
        user.SetStripeSubscription("sub_yearly", DateTime.UtcNow.AddYears(1), SubscriptionInterval.Yearly);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseRetrospective_MonthlySubscription_Success()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseRetrospective_LifetimePro_Success()
    {
        var user = CreateFreeUser();
        user.GrantLifetimePro();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseRetrospective_ActiveTrial_Success()
    {
        var user = CreateFreeUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseRetrospective_FreeUser_PayGateFailure()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        result.Error.Should().Be("Retrospectives are a Pro feature. Upgrade to unlock!");
    }

    [Fact]
    public async Task CanUseRetrospective_ExpiredSubscription_PayGateFailure()
    {
        var user = CreateFreeUser();
        user.SetStripeSubscription("sub_expired", DateTime.UtcNow.AddDays(-1), SubscriptionInterval.Monthly);
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        result.Error.Should().Be("Retrospectives are a Pro feature. Upgrade to unlock!");
    }

    [Fact]
    public async Task CanUseRetrospective_UserNotFound_Failure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task CanUseRetrospective_ConfigDisabled_FreeUserAllowed()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _appConfig.GetAsync("RetrospectiveProOnly", true, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateGoals_ProUser_Success()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanCreateGoals(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateGoals_FreeUser_PayGateFailure()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanCreateGoals(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanCreateGoals_UserNotFound_Failure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.CanCreateGoals(UserId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task CanCreateGoals_ConfigDisabled_FreeUserAllowed()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _appConfig.GetAsync("GoalsProOnly", true, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CanCreateGoals(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateApiKeys_ProUser_Success()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanCreateApiKeys(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateApiKeys_FreeUser_PayGateFailure()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanCreateApiKeys(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanCreateApiKeys_UserNotFound_Failure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.CanCreateApiKeys(UserId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task CanCreateHabits_TrialUser_HasProAccess()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(7)); _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanCreateHabits(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateSubHabits_TrialUser_HasProAccess()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(7));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanCreateSubHabits(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanSendAiMessage_WithAdRewardBonus_IncreasedLimit()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (int i = 0; i < 20; i++)
            user.IncrementAiMessageCount();
        user.GrantAdReward(DateOnly.FromDateTime(DateTime.UtcNow), 5);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanSendAiMessage(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetAiMessageLimit_WithAdRewardBonus_IncludesBonus()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.GrantAdReward(DateOnly.FromDateTime(DateTime.UtcNow), 5);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var limit = await _sut.GetAiMessageLimit(UserId);

        limit.Should().Be(25);
    }

    [Fact]
    public async Task CanUseDailySummary_ConfigDisabled_FreeUserAllowed()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _appConfig.GetAsync("DailySummaryProOnly", true, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CanUseDailySummary(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseDailySummary_UserNotFound_Failure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.CanUseDailySummary(UserId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task CanSendAiMessage_UserNotFound_Failure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.CanSendAiMessage(UserId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task CanCreateSubHabits_UserNotFound_Failure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.CanCreateSubHabits(UserId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    private static async Task WithEnvironment(string aspNetEnv, string? smokeEmail, Func<Task> body)
    {
        var priorEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var priorEmail = Environment.GetEnvironmentVariable("SMOKE_TEST_EMAIL");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspNetEnv);
        Environment.SetEnvironmentVariable("SMOKE_TEST_EMAIL", smokeEmail);
        try
        {
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", priorEnv);
            Environment.SetEnvironmentVariable("SMOKE_TEST_EMAIL", priorEmail);
        }
    }
}
