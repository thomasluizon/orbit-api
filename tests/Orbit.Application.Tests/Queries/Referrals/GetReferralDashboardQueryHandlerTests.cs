using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Referrals;

public class GetReferralDashboardQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Referral> _referralRepo = Substitute.For<IGenericRepository<Referral>>();
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly IOptions<FrontendSettings> _frontendSettings;
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly GetReferralDashboardQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetReferralDashboardQueryHandlerTests()
    {
        _frontendSettings = Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" });
        _handler = new GetReferralDashboardQueryHandler(_userRepo, _referralRepo, _appConfigService, _frontendSettings, _mediator);
    }

    private static User CreateTestUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetReferralCode("TESTCODE");
        return user;
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsDashboard()
    {
        var user = CreateTestUser();

        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("TESTCODE"));

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>().AsReadOnly());

        _appConfigService.GetAsync("MaxReferrals", AppConstants.DefaultMaxReferrals, Arg.Any<CancellationToken>())
            .Returns(10);

        var query = new GetReferralDashboardQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("TESTCODE");
        result.Value.Link.Should().Contain("TESTCODE");
        result.Value.Stats.Should().NotBeNull();
        result.Value.Stats.SuccessfulReferrals.Should().Be(0);
        result.Value.Stats.PendingReferrals.Should().Be(0);
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("TESTCODE"));

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>().AsReadOnly());

        _appConfigService.GetAsync("MaxReferrals", AppConstants.DefaultMaxReferrals, Arg.Any<CancellationToken>())
            .Returns(10);

        var query = new GetReferralDashboardQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_ReferralCodeCreationFails_ReturnsFailure()
    {
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("Failed to create referral code"));

        var query = new GetReferralDashboardQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Failed to create referral code");
    }

    [Fact]
    public async Task Handle_LinkContainsBaseUrl()
    {
        var user = CreateTestUser();

        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("TESTCODE"));

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>().AsReadOnly());

        _appConfigService.GetAsync("MaxReferrals", AppConstants.DefaultMaxReferrals, Arg.Any<CancellationToken>())
            .Returns(10);

        var query = new GetReferralDashboardQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Link.Should().Be("https://app.useorbit.org/r/TESTCODE");
    }
}
