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

        _mediator.Send(Arg.Any<RunCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CalendarAutoSyncResult(0, 0, Domain.Enums.GoogleCalendarAutoSyncStatus.Idle)));

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

    [Fact]
    public async Task GetEvents_Success_OpportunisticSyncFails_StillReturnsOk()
    {
        _mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarEventItem>()));

        _mediator.Send(Arg.Any<RunCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns<Result<CalendarAutoSyncResult>>(x => throw new InvalidOperationException("Sync exploded"));

        var result = await _controller.GetEvents(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
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

    // --- GetAutoSyncState ---

    [Fact]
    public async Task GetAutoSyncState_Success_ReturnsOk()
    {
        var response = new CalendarAutoSyncStateResponse(true, Domain.Enums.GoogleCalendarAutoSyncStatus.Idle, null, true);
        _mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _controller.GetAutoSyncState(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetAutoSyncState_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CalendarAutoSyncStateResponse>("Error"));

        var result = await _controller.GetAutoSyncState(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- SetAutoSync ---

    [Fact]
    public async Task SetAutoSync_ValidEnabled_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SetCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new CalendarController.SetAutoSyncRequest(true);
        var result = await _controller.SetAutoSync(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SetAutoSync_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SetCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not connected", "CALENDAR_NOT_CONNECTED"));

        var request = new CalendarController.SetAutoSyncRequest(true);
        var result = await _controller.SetAutoSync(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- GetSuggestions ---

    [Fact]
    public async Task GetSuggestions_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarSyncSuggestionItem>()));

        var result = await _controller.GetSuggestions(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetSuggestions_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<List<CalendarSyncSuggestionItem>>("Error"));

        var result = await _controller.GetSuggestions(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- DismissSuggestion ---

    [Fact]
    public async Task DismissSuggestion_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DismissCalendarSuggestionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.DismissSuggestion(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DismissSuggestion_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DismissCalendarSuggestionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not found"));

        var result = await _controller.DismissSuggestion(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- RunSyncNow ---

    [Fact]
    public async Task RunSyncNow_Success_ReturnsOk()
    {
        var syncResult = new CalendarAutoSyncResult(0, 0, Domain.Enums.GoogleCalendarAutoSyncStatus.Idle);
        _mediator.Send(Arg.Any<RunCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(syncResult));

        var result = await _controller.RunSyncNow(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RunSyncNow_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<RunCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CalendarAutoSyncResult>("Sync failed", "SYNC_FAILED"));

        var result = await _controller.RunSyncNow(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
