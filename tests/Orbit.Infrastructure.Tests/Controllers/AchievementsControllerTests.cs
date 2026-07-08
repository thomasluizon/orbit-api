using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Gamification.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AchievementsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly AchievementsController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public AchievementsControllerTests()
    {
        _controller = new AchievementsController(_mediator);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task ReportEvent_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<ReportEventCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<ReportEventResponse>(default!));

        var body = new AchievementsController.ReportEventBody("habit_completed");
        var result = await _controller.ReportEvent(body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ReportEvent_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<ReportEventCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ReportEventResponse>("Invalid event"));

        var body = new AchievementsController.ReportEventBody("");
        var result = await _controller.ReportEvent(body, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }
}
