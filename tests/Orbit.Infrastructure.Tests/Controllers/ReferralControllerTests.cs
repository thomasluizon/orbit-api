using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ReferralControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IOptions<FrontendSettings> _frontendSettings;
    private readonly ReferralController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public ReferralControllerTests()
    {
        _frontendSettings = Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" });
        _controller = new ReferralController(_mediator, _frontendSettings);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetOrCreateCode ---

    [Fact]
    public async Task GetOrCreateCode_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("ABC123"));

        var result = await _controller.GetOrCreateCode(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetOrCreateCode_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("Error"));

        var result = await _controller.GetOrCreateCode(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- GetStats ---

    [Fact]
    public async Task GetStats_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetReferralStatsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(ReferralStatsResponse)!));

        var result = await _controller.GetStats(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetStats_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetReferralStatsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ReferralStatsResponse>("Error"));

        var result = await _controller.GetStats(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- GetDashboard ---

    [Fact]
    public async Task GetDashboard_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetReferralDashboardQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(ReferralDashboardResponse)!));

        var result = await _controller.GetDashboard(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetDashboard_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetReferralDashboardQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ReferralDashboardResponse>("Error"));

        var result = await _controller.GetDashboard(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
