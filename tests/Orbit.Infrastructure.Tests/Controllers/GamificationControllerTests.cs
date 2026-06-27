using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Controllers;

public class GamificationControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GamificationController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public GamificationControllerTests()
    {
        _controller = new GamificationController(_mediator, _userDateService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

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

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetStreakInfo_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<StreakInfoResponse>("Pro required"));

        var result = await _controller.GetStreakInfo(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task GetRecap_Success_ReturnsOk()
    {
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 6, 20));
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(1);
        _mediator.Send(Arg.Any<GetRecapQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(RecapResponse)!));

        var result = await _controller.GetRecap("week", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
