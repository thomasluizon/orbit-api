using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

[Collection("ProcessEnvironment")]
public class PayGateServiceTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IAppConfigService _appConfig = Substitute.For<IAppConfigService>();
    private readonly PayGateService _sut;

    private static readonly Guid UserId = Guid.NewGuid();

    public PayGateServiceTests()
    {
        _sut = new PayGateService(_habitRepo, _userRepo, _appConfig);

        _appConfig.GetAsync("FreeMaxHabits", 10, Arg.Any<CancellationToken>()).Returns(10);
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
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddYears(1));
        return user;
    }

    [Fact]
    public async Task CanCreateHabits_ProUser_AlwaysSuccess()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanCreateHabits(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateHabits_FreeUser_UnderLimit_Success()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitRepo.CountAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await _sut.CanCreateHabits(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateHabits_FreeUser_AtLimit_PayGateFailure()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.CountAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(10);

        var result = await _sut.CanCreateHabits(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanCreateHabits_UserNotFound_Failure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.CanCreateHabits(UserId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
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
    public async Task CanUseRetrospective_YearlyProUser_Success()
    {
        var user = CreateFreeUser();
        user.SetStripeSubscription("sub_yearly", DateTime.UtcNow.AddYears(1), SubscriptionInterval.Yearly);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseRetrospective_MonthlyProUser_PayGateFailure()
    {
        var user = CreateProUser();        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.CanUseRetrospective(UserId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
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
        user.StartTrial(DateTime.UtcNow.AddDays(7));        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

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

        limit.Should().Be(25);    }

    [Fact]
    public async Task CanCreateHabits_FreeUser_BulkCreate_ExceedsLimit_PayGateFailure()
    {
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.CountAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(8);

        var result = await _sut.CanCreateHabits(UserId, 3);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
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
