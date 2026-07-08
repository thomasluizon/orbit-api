using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Marketing.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AdminControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<AdminController> _logger = Substitute.For<ILogger<AdminController>>();
    private readonly AdminController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public AdminControllerTests()
    {
        _controller = new AdminController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    private static AdminController.BroadcastRequest NewRequest() =>
        new("Subject", "Assunto", "<p>en</p>", "<p>pt</p>", null);

    [Fact]
    public async Task SendMarketingBroadcast_Success_ReturnsAccepted()
    {
        _mediator.Send(Arg.Any<SendMarketingBroadcastCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new MarketingBroadcastResult(5, false)));

        var result = await _controller.SendMarketingBroadcast(NewRequest(), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task SendMarketingBroadcast_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SendMarketingBroadcastCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<MarketingBroadcastResult>("Subject required"));

        var result = await _controller.SendMarketingBroadcast(NewRequest(), CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }
}
