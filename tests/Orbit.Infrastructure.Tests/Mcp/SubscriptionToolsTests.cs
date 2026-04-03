using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Mcp;

public class SubscriptionToolsTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly SubscriptionTools _tools;
    private readonly ClaimsPrincipal _user;
    private readonly Guid _userId = Guid.NewGuid();

    public SubscriptionToolsTests()
    {
        var frontendSettings = Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" });
        _tools = new SubscriptionTools(_userRepo, _payGate, _mediator, frontendSettings);

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, _userId.ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    // --- GetSubscriptionStatus ---

    [Fact]
    public async Task GetSubscriptionStatus_UserNotFound_ReturnsError()
    {
        _userRepo.GetByIdAsync(_userId, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _tools.GetSubscriptionStatus(_user);

        result.Should().Contain("Error:");
    }

    [Fact]
    public async Task GetSubscriptionStatus_UserWithTrial_ShowsPlanAndAiMessages()
    {
        // User.Create gives a 7-day trial by default, so HasProAccess is true
        var user = User.Create("Thomas", "thomas@example.com").Value;
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(5);

        var result = await _tools.GetSubscriptionStatus(_user);

        result.Should().Contain("Plan: Pro");
        result.Should().Contain("AI Messages: 0/5");
    }

    [Fact]
    public async Task GetSubscriptionStatus_TrialActive_ShowsTrialInfo()
    {
        // User.Create gives a 7-day trial by default
        var user = User.Create("Thomas", "thomas@example.com").Value;
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(100);

        var result = await _tools.GetSubscriptionStatus(_user);

        // New users have a trial active
        result.Should().Contain("Trial active");
    }

    [Fact]
    public async Task GetSubscriptionStatus_AiMessageUsage_ShowsCorrectCount()
    {
        var user = User.Create("Thomas", "thomas@example.com").Value;
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(50);

        var result = await _tools.GetSubscriptionStatus(_user);

        result.Should().Contain("AI Messages: 0/50");
    }

    // --- GetReferralStats ---

    [Fact]
    public async Task GetReferralStats_Success_ReturnsFormattedStats()
    {
        var stats = new ReferralStatsResponse(
            "ABC123", "https://app.useorbit.org/r/ABC123",
            3, 1, 10, "discount", 20);

        _mediator.Send(Arg.Any<GetReferralStatsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(stats));

        var result = await _tools.GetReferralStats(_user);

        result.Should().Contain("Referral Code: ABC123");
        result.Should().Contain("Successful Referrals: 3");
        result.Should().Contain("Pending Referrals: 1");
        result.Should().Contain("Max Referrals: 10");
        result.Should().Contain("20% discount");
    }

    [Fact]
    public async Task GetReferralStats_NoCode_OmitsCodeAndLink()
    {
        var stats = new ReferralStatsResponse(
            null, null, 0, 0, 10, "discount", 20);

        _mediator.Send(Arg.Any<GetReferralStatsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(stats));

        var result = await _tools.GetReferralStats(_user);

        result.Should().NotContain("Referral Code:");
        result.Should().NotContain("Referral Link:");
        result.Should().Contain("Successful Referrals: 0");
    }

    [Fact]
    public async Task GetReferralStats_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetReferralStatsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ReferralStatsResponse>("User not found"));

        var result = await _tools.GetReferralStats(_user);

        result.Should().StartWith("Error: ");
    }

    // --- GetReferralCode ---

    [Fact]
    public async Task GetReferralCode_Success_ReturnsCodeAndLink()
    {
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("XYZ789"));

        var result = await _tools.GetReferralCode(_user);

        result.Should().Contain("Referral Code: XYZ789");
        result.Should().Contain("Link: https://app.useorbit.org/r/XYZ789");
    }

    [Fact]
    public async Task GetReferralCode_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("User not found"));

        var result = await _tools.GetReferralCode(_user);

        result.Should().StartWith("Error: ");
    }

    // --- GetUserId ---

    [Fact]
    public async Task AnyMethod_MissingUserClaim_ThrowsUnauthorized()
    {
        var emptyUser = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _tools.GetSubscriptionStatus(emptyUser);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*User ID not found*");
    }
}
