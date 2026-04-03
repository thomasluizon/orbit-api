using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

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

        // Default config values
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

    // --- CanCreateHabits ---

    [Fact]
    public async Task CanCreateHabits_ProUser_AlwaysSuccess()
    {
        // Arrange
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanCreateHabits(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateHabits_FreeUser_UnderLimit_Success()
    {
        // Arrange
        var user = CreateFreeUser();
        // Trial ends in the past so user is truly free
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());

        // Act
        var result = await _sut.CanCreateHabits(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateHabits_FreeUser_AtLimit_PayGateFailure()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Create 10 habits (at limit)
        var habits = Enumerable.Range(0, 10)
            .Select(_ => Habit.Create(new HabitCreateParams(UserId, "h", FrequencyUnit.Day, 1)).Value)
            .ToList();
        _habitRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habits);

        // Act
        var result = await _sut.CanCreateHabits(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanCreateHabits_UserNotFound_Failure()
    {
        // Arrange
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        // Act
        var result = await _sut.CanCreateHabits(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    // --- CanCreateSubHabits ---

    [Fact]
    public async Task CanCreateSubHabits_ProUser_Success()
    {
        // Arrange
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanCreateSubHabits(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateSubHabits_FreeUser_PayGateFailure()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanCreateSubHabits(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanCreateSubHabits_ConfigDisabled_FreeUserAllowed()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _appConfig.GetAsync("SubHabitsProOnly", true, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _sut.CanCreateSubHabits(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    // --- CanSendAiMessage ---

    [Fact]
    public async Task CanSendAiMessage_UnderLimit_Success()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        // AiMessagesUsedThisMonth defaults to 0, under limit of 20

        // Act
        var result = await _sut.CanSendAiMessage(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanSendAiMessage_AtLimit_PayGateFailure()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        // Increment messages to hit the limit (20)
        for (int i = 0; i < 20; i++)
            user.IncrementAiMessageCount();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanSendAiMessage(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    // --- CanUseDailySummary ---

    [Fact]
    public async Task CanUseDailySummary_ProUser_Success()
    {
        // Arrange
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanUseDailySummary(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseDailySummary_FreeUser_PayGateFailure()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanUseDailySummary(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    // --- GetAiMessageLimit ---

    [Fact]
    public async Task GetAiMessageLimit_ProUser_ReturnsProLimit()
    {
        // Arrange
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var limit = await _sut.GetAiMessageLimit(UserId);

        // Assert
        limit.Should().Be(500);
    }

    [Fact]
    public async Task GetAiMessageLimit_FreeUser_ReturnsFreeLimit()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var limit = await _sut.GetAiMessageLimit(UserId);

        // Assert
        limit.Should().Be(20);
    }

    [Fact]
    public async Task GetAiMessageLimit_UserNotFound_ReturnsDefault20()
    {
        // Arrange
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        // Act
        var limit = await _sut.GetAiMessageLimit(UserId);

        // Assert
        limit.Should().Be(20);
    }

    // --- CanUseRetrospective ---

    [Fact]
    public async Task CanUseRetrospective_YearlyProUser_Success()
    {
        // Arrange
        var user = CreateFreeUser();
        user.SetStripeSubscription("sub_yearly", DateTime.UtcNow.AddYears(1), SubscriptionInterval.Yearly);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanUseRetrospective(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseRetrospective_MonthlyProUser_PayGateFailure()
    {
        // Arrange
        var user = CreateProUser(); // monthly by default
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanUseRetrospective(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanUseRetrospective_FreeUser_PayGateFailure()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanUseRetrospective(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanUseRetrospective_UserNotFound_Failure()
    {
        // Arrange
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        // Act
        var result = await _sut.CanUseRetrospective(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task CanUseRetrospective_ConfigDisabled_FreeUserAllowed()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _appConfig.GetAsync("RetrospectiveProOnly", true, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _sut.CanUseRetrospective(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    // --- CanCreateGoals ---

    [Fact]
    public async Task CanCreateGoals_ProUser_Success()
    {
        // Arrange
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanCreateGoals(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateGoals_FreeUser_PayGateFailure()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanCreateGoals(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanCreateGoals_UserNotFound_Failure()
    {
        // Arrange
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        // Act
        var result = await _sut.CanCreateGoals(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task CanCreateGoals_ConfigDisabled_FreeUserAllowed()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _appConfig.GetAsync("GoalsProOnly", true, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _sut.CanCreateGoals(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    // --- CanCreateApiKeys ---

    [Fact]
    public async Task CanCreateApiKeys_ProUser_Success()
    {
        // Arrange
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanCreateApiKeys(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateApiKeys_FreeUser_PayGateFailure()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanCreateApiKeys(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanCreateApiKeys_UserNotFound_Failure()
    {
        // Arrange
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        // Act
        var result = await _sut.CanCreateApiKeys(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    // --- Trial user scenarios ---

    [Fact]
    public async Task CanCreateHabits_TrialUser_HasProAccess()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(7)); // Trial active
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanCreateHabits(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateSubHabits_TrialUser_HasProAccess()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(7));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanCreateSubHabits(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    // --- AI message limit with ad reward bonus ---

    [Fact]
    public async Task CanSendAiMessage_WithAdRewardBonus_IncreasedLimit()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        // Use 20 AI messages (at the free limit)
        for (int i = 0; i < 20; i++)
            user.IncrementAiMessageCount();
        // Add ad reward bonus (5 extra)
        user.GrantAdReward(5);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _sut.CanSendAiMessage(UserId);

        // Assert - should succeed because bonus extends the limit
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetAiMessageLimit_WithAdRewardBonus_IncludesBonus()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.GrantAdReward(5);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var limit = await _sut.GetAiMessageLimit(UserId);

        // Assert
        limit.Should().Be(25); // 20 base + 5 bonus
    }

    // --- CanCreateHabits with count parameter ---

    [Fact]
    public async Task CanCreateHabits_FreeUser_BulkCreate_ExceedsLimit_PayGateFailure()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var habits = Enumerable.Range(0, 8)
            .Select(_ => Habit.Create(new HabitCreateParams(UserId, "h", FrequencyUnit.Day, 1)).Value)
            .ToList();
        _habitRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habits);

        // Act - trying to create 3 more (8 + 3 = 11 > 10)
        var result = await _sut.CanCreateHabits(UserId, 3);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CanUseDailySummary_ConfigDisabled_FreeUserAllowed()
    {
        // Arrange
        var user = CreateFreeUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _appConfig.GetAsync("DailySummaryProOnly", true, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _sut.CanUseDailySummary(UserId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CanUseDailySummary_UserNotFound_Failure()
    {
        // Arrange
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        // Act
        var result = await _sut.CanUseDailySummary(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task CanSendAiMessage_UserNotFound_Failure()
    {
        // Arrange
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        // Act
        var result = await _sut.CanSendAiMessage(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task CanCreateSubHabits_UserNotFound_Failure()
    {
        // Arrange
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        // Act
        var result = await _sut.CanCreateSubHabits(UserId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }
}
