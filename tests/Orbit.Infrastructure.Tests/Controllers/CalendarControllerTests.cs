using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class CalendarControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<CalendarController> _logger = Substitute.For<ILogger<CalendarController>>();
    private readonly CalendarController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public CalendarControllerTests()
    {
        _controller = new CalendarController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetEvents ---

    [Fact]
    public async Task GetEvents_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarEventItem>()));

        var result = await _controller.GetEvents(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetEvents_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<List<CalendarEventItem>>("Error"));

        var result = await _controller.GetEvents(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- DismissImport ---

    [Fact]
    public async Task DismissImport_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DismissCalendarImportCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.DismissImport(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DismissImport_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DismissCalendarImportCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.DismissImport(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
