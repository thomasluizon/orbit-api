using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class GamificationControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly GamificationController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public GamificationControllerTests()
    {
        _controller = new GamificationController(_mediator);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetProfile ---

    [Fact]
    public async Task GetProfile_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(GamificationProfileResponse)!));

        var result = await _controller.GetProfile(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetProfile_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<GamificationProfileResponse>("Pro required"));

        var result = await _controller.GetProfile(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- GetAchievements ---

    [Fact]
    public async Task GetAchievements_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(AchievementsResponse)!));

        var result = await _controller.GetAchievements(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetAchievements_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<AchievementsResponse>("Pro required"));

        var result = await _controller.GetAchievements(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- GetStreakInfo ---

    [Fact]
    public async Task GetStreakInfo_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(StreakInfoResponse)!));

        var result = await _controller.GetStreakInfo(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetStreakInfo_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<StreakInfoResponse>("Error"));

        var result = await _controller.GetStreakInfo(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- ActivateStreakFreeze ---

    [Fact]
    public async Task ActivateStreakFreeze_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<ActivateStreakFreezeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(StreakFreezeResponse)!));

        var result = await _controller.ActivateStreakFreeze(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ActivateStreakFreeze_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<ActivateStreakFreezeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<StreakFreezeResponse>("No freeze available"));

        var result = await _controller.ActivateStreakFreeze(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
