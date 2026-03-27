using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Referrals;

public class GetReferralStatsQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Referral> _referralRepo = Substitute.For<IGenericRepository<Referral>>();
    private readonly IAppConfigService _appConfig = Substitute.For<IAppConfigService>();
    private readonly GetReferralStatsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetReferralStatsQueryHandlerTests()
    {
        _handler = new GetReferralStatsQueryHandler(_userRepo, _referralRepo, _appConfig);

        _appConfig.GetAsync("MaxReferrals", AppConstants.DefaultMaxReferrals, Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultMaxReferrals);
        _appConfig.GetAsync("ReferralRewardDays", AppConstants.DefaultReferralRewardDays, Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultReferralRewardDays);
    }

    private static User CreateTestUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, UserId);
        return user;
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var query = new GetReferralStatsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound);
    }

    [Fact]
    public async Task Handle_NoReferrals_ReturnsZeroCounts()
    {
        var user = CreateTestUser();
        user.SetReferralCode("CODE1234");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>());

        var query = new GetReferralStatsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SuccessfulReferrals.Should().Be(0);
        result.Value.PendingReferrals.Should().Be(0);
        result.Value.MaxReferrals.Should().Be(AppConstants.DefaultMaxReferrals);
        result.Value.RewardDays.Should().Be(AppConstants.DefaultReferralRewardDays);
    }

    [Fact]
    public async Task Handle_MixedReferrals_ReturnsCorrectCounts()
    {
        var user = CreateTestUser();
        user.SetReferralCode("CODE1234");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        var pending1 = Referral.Create(UserId, Guid.NewGuid());
        var pending2 = Referral.Create(UserId, Guid.NewGuid());
        var completed = Referral.Create(UserId, Guid.NewGuid());
        completed.MarkCompleted();
        var rewarded = Referral.Create(UserId, Guid.NewGuid());
        rewarded.MarkCompleted();
        rewarded.MarkRewarded();

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral> { pending1, pending2, completed, rewarded });

        var query = new GetReferralStatsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SuccessfulReferrals.Should().Be(2); // Completed + Rewarded
        result.Value.PendingReferrals.Should().Be(2);
    }

    [Fact]
    public async Task Handle_WithReferralCode_ReturnsReferralLink()
    {
        var user = CreateTestUser();
        user.SetReferralCode("ABC12345");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>());

        var query = new GetReferralStatsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ReferralCode.Should().Be("ABC12345");
        result.Value.ReferralLink.Should().Be("https://app.useorbit.org/r/ABC12345");
    }

    [Fact]
    public async Task Handle_WithoutReferralCode_ReturnsNullLink()
    {
        var user = CreateTestUser();
        // No referral code set
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>());

        var query = new GetReferralStatsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ReferralCode.Should().BeNull();
        result.Value.ReferralLink.Should().BeNull();
    }
}
