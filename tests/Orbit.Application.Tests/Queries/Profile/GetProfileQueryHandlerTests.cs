using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Profile;

public class GetProfileQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly GetProfileQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetProfileQueryHandlerTests()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _handler = new GetProfileQueryHandler(_userRepo, _streakFreezeRepo, _userDateService, _featureFlagService, _payGate);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static User CreateTestUser(string name = "Test User")
    {
        return User.Create(name, "test@example.com").Value;
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private void EnableFreeTierFlag()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.GamificationFreeTier });
    }

    private void StubFreezeRepoEmpty()
    {
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsProfile()
    {
        var user = CreateTestUser("John Doe");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("John Doe");
        result.Value.Email.Should().Be("test@example.com");
        result.Value.AiMessagesLimit.Should().Be(20);
    }

    [Fact]
    public async Task Handle_ReturnsOnboardingChecklistFlags()
    {
        var user = CreateTestUser();
        user.MarkFirstHabitCreated();
        user.MarkAstraUsed();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        StubFreezeRepoEmpty();

        var result = await _handler.Handle(new GetProfileQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasCreatedFirstHabit.Should().BeTrue();
        result.Value.HasLoggedFirstHabit.Should().BeFalse();
        result.Value.HasTriedAstra.Should().BeTrue();
        result.Value.HasCompletedOnboardingChecklist.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_UserWithStreak_ReturnsCurrentAndLongest()
    {
        var user = CreateTestUser();
        user.SetStreakState(5, 12, Today);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(5);
        result.Value.LongestStreak.Should().Be(12);
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_FreeUser_ReturnsFreeplan()
    {
        var user = CreateTestUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().Be("free");
        result.Value.HasProAccess.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ProUser_ReturnsProPlan()
    {
        var user = CreateTestUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddYears(1));

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(500);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
        result.Value.HasProAccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NoRecentFreezes_ReturnsMaxAvailable()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.StreakFreezesAvailable.Should().Be(3);
    }

    [Fact]
    public async Task Handle_AllFreezesUsed_ReturnsZeroAvailable()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);

        var recentFreezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today.AddDays(-1)),
            StreakFreeze.Create(UserId, Today.AddDays(-5)),
            StreakFreeze.Create(UserId, Today.AddDays(-10))
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(recentFreezes.AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.StreakFreezesAvailable.Should().Be(0);
    }

    [Fact]
    public async Task Handle_Returns24HourClock_ForBrazilTimeZone()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var result = await _handler.Handle(new GetProfileQuery(UserId), CancellationToken.None);

        result.Value.Uses24HourClock.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Returns12HourClock_ForUnitedStatesTimeZone()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/New_York");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var result = await _handler.Handle(new GetProfileQuery(UserId), CancellationToken.None);

        result.Value.Uses24HourClock.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ProUser_CanViewGamificationTrue()
    {
        var user = CreateTestUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddYears(1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(500);
        StubFreezeRepoEmpty();

        var result = await _handler.Handle(new GetProfileQuery(UserId), CancellationToken.None);

        result.Value.CanViewGamification.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_FreeUser_FlagOn_CanViewGamificationTrue()
    {
        var user = CreateFreeUser();
        EnableFreeTierFlag();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        StubFreezeRepoEmpty();

        var result = await _handler.Handle(new GetProfileQuery(UserId), CancellationToken.None);

        result.Value.CanViewGamification.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_FreeUser_FlagOff_CanViewGamificationFalse()
    {
        var user = CreateFreeUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        StubFreezeRepoEmpty();

        var result = await _handler.Handle(new GetProfileQuery(UserId), CancellationToken.None);

        result.Value.CanViewGamification.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_Past10Xp_ComputesLevelFromXp()
    {
        var user = CreateTestUser();
        user.AddXp(15_000);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        StubFreezeRepoEmpty();

        var result = await _handler.Handle(new GetProfileQuery(UserId), CancellationToken.None);

        result.Value.Level.Should().Be(12);
        result.Value.LevelTitle.Should().Be("Legend");
    }
}
