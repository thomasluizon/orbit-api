using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Referrals;

public class ProcessReferralCodeCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Referral> _referralRepo = Substitute.For<IGenericRepository<Referral>>();
    private readonly IAppConfigService _appConfig = Substitute.For<IAppConfigService>();
    private readonly IReferralRewardService _referralReward = Substitute.For<IReferralRewardService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ProcessReferralCodeCommandHandler _handler;

    private static readonly Guid ReferrerId = Guid.NewGuid();
    private static readonly Guid NewUserId = Guid.NewGuid();

    public ProcessReferralCodeCommandHandlerTests()
    {
        _handler = new ProcessReferralCodeCommandHandler(
            _userRepo, _referralRepo, _appConfig, _referralReward, _unitOfWork);

        // Default config values
        _appConfig.GetAsync("MaxReferrals", AppConstants.DefaultMaxReferrals, Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultMaxReferrals);

        _referralReward.CreateReferralCouponAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("promo_test456");
    }

    private static User CreateReferrer()
    {
        var user = User.Create("Referrer", "referrer@example.com").Value;
        user.SetReferralCode("TESTCODE");
        typeof(User).GetProperty("Id")!.SetValue(user, ReferrerId);
        return user;
    }

    private static User CreateNewUser()
    {
        var user = User.Create("New User", "newuser@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, NewUserId);
        return user;
    }

    /// <summary>
    /// Sets up FindOneTrackedAsync to return the correct user based on the predicate.
    /// We use Returns with a callback to handle multiple calls with different predicates.
    /// </summary>
    private void SetupUsersFound(User referrer, User newUser)
    {
        // First call finds referrer by code, second finds new user by ID
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(referrer, newUser);
    }

    [Fact]
    public async Task Handle_ValidCode_LinksReferrerAndCreatesCouponAndReferral()
    {
        var referrer = CreateReferrer();
        var newUser = CreateNewUser();
        SetupUsersFound(referrer, newUser);

        // No existing successful referrals
        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>());

        var command = new ProcessReferralCodeCommand(NewUserId, "TESTCODE");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        newUser.ReferredByUserId.Should().Be(ReferrerId);
        await _referralRepo.Received(1).AddAsync(
            Arg.Is<Referral>(r => r.ReferrerId == ReferrerId && r.ReferredUserId == NewUserId),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ValidCode_CreatesCouponForNewUser()
    {
        var referrer = CreateReferrer();
        var newUser = CreateNewUser();
        SetupUsersFound(referrer, newUser);

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>());

        var command = new ProcessReferralCodeCommand(NewUserId, "TESTCODE");

        await _handler.Handle(command, CancellationToken.None);

        // Coupon should be created for the new user
        await _referralReward.Received(1).CreateReferralCouponAsync(
            NewUserId, Arg.Any<CancellationToken>());
        newUser.ReferralCouponId.Should().Be("promo_test456");
    }

    [Fact]
    public async Task Handle_InvalidCode_ReturnsFailure()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var command = new ProcessReferralCodeCommand(NewUserId, "BADCODE");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.InvalidReferralCode);
        await _referralRepo.DidNotReceive().AddAsync(Arg.Any<Referral>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MaxReferralsReached_ReturnsFailure()
    {
        var referrer = CreateReferrer();
        var newUser = CreateNewUser();
        SetupUsersFound(referrer, newUser);

        // Return max number of successful referrals
        var completedReferrals = Enumerable.Range(0, AppConstants.DefaultMaxReferrals)
            .Select(_ =>
            {
                var r = Referral.Create(ReferrerId, Guid.NewGuid());
                r.MarkCompleted();
                return r;
            })
            .ToList();

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(completedReferrals);

        var command = new ProcessReferralCodeCommand(NewUserId, "TESTCODE");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.ReferralCapReached);
    }

    [Fact]
    public async Task Handle_SelfReferral_ReturnsFailure()
    {
        var referrer = CreateReferrer();
        // The referrer is also the "new user" (same ID)
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(referrer);

        var command = new ProcessReferralCodeCommand(ReferrerId, "TESTCODE");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.SelfReferral);
    }

    [Fact]
    public async Task Handle_AlreadyReferred_ReturnsFailure()
    {
        var referrer = CreateReferrer();
        var newUser = CreateNewUser();
        newUser.SetReferredBy(Guid.NewGuid()); // Already referred by someone else
        SetupUsersFound(referrer, newUser);

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>());

        var command = new ProcessReferralCodeCommand(NewUserId, "TESTCODE");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AlreadyReferred);
    }
}
