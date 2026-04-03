using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Calendar.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Mcp;

public class CalendarToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly CalendarTools _tools;
    private readonly ClaimsPrincipal _user;

    public CalendarToolsTests()
    {
        _tools = new CalendarTools(_mediator);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task GetCalendarEvents_Success_ReturnsFormattedEvents()
    {
        var events = new List<CalendarEventItem>
        {
            new("evt-1", "Team Meeting", "Weekly sync", "2026-04-03", "10:00", "11:00", true, null, [15])
        };

        _mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(events));

        var result = await _tools.GetCalendarEvents(_user);

        result.Should().Contain("Team Meeting");
        result.Should().Contain("2026-04-03");
        result.Should().Contain("10:00");
        result.Should().Contain("recurring");
        result.Should().Contain("Calendar Events (1)");
    }

    [Fact]
    public async Task GetCalendarEvents_Empty_ReturnsNoEventsMessage()
    {
        _mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarEventItem>()));

        var result = await _tools.GetCalendarEvents(_user);

        result.Should().Contain("No upcoming calendar events found");
    }

    [Fact]
    public async Task GetCalendarEvents_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<List<CalendarEventItem>>("Calendar not connected"));

        var result = await _tools.GetCalendarEvents(_user);

        result.Should().StartWith("Error: ");
    }
}
