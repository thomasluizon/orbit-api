using System.Linq.Expressions;
using FluentAssertions;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Calendar;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Runs the real <see cref="GoogleCalendarEventFetcher"/> into the real
/// <see cref="GetCalendarEventsQueryHandler"/> over a Google payload shaped like the one
/// <c>GoogleCalendarApi</c> can actually return, so the recurrence gate is judged on evidence the
/// production path can supply rather than on a hand-built item.
/// </summary>
public class CalendarProjectionEndToEndTests
{
    private readonly IGoogleCalendarApi _api = Substitute.For<IGoogleCalendarApi>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo =
        Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGoogleTokenService _tokenService = Substitute.For<IGoogleTokenService>();
    private readonly GetCalendarEventsQueryHandler _handler;

    private const string Token = "access-token";

    public CalendarProjectionEndToEndTests()
    {
        _payGate.CanAccessCalendar(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _suggestionRepo.FindAsync(
                Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());

        var fetcher = new GoogleCalendarEventFetcher(
            _api, Substitute.For<ILogger<GoogleCalendarEventFetcher>>());

        _handler = new GetCalendarEventsQueryHandler(
            new GetCalendarEventsRepositories(_userRepo, _habitRepo, _suggestionRepo),
            _payGate,
            _tokenService,
            fetcher,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<ILogger<GetCalendarEventsQueryHandler>>());
    }

    /// <summary>
    /// <c>GoogleCalendarApi</c> asks for 60 days, so a January fetch of a <c>Europe/Lisbon</c> 03:30
    /// <c>BYDAY=TH</c> series never reaches the 2027-03-29 Lisbon transition. Every occurrence inside
    /// that window is Thursday in <c>America/Sao_Paulo</c> too, and every occurrence after it is
    /// Wednesday 23:30, so a window that proves nothing must not admit the series.
    /// </summary>
    [Fact]
    public async Task ByDaySeriesWhoseTransitionFallsOutsideTheFetchWindow_IsWithheld()
    {
        var user = CreateUser("America/Sao_Paulo");
        StubLisbonSeries(LisbonThursdaysBeforeTheTransition());

        var result = await _handler.Handle(new GetCalendarEventsQuery(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    /// <summary>
    /// The same fetch for an account in the calendar's own zone keeps flowing: the projection is the
    /// identity, so no occurrence can name a different weekday.
    /// </summary>
    [Fact]
    public async Task ByDaySeriesReadFromItsOwnZone_IsOffered()
    {
        var user = CreateUser("Europe/Lisbon");
        StubLisbonSeries(LisbonThursdaysBeforeTheTransition());

        var result = await _handler.Handle(new GetCalendarEventsQuery(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=DAILY;BYDAY=TH");
        result.Value[0].StartDate.Should().Be("2027-01-07");
        result.Value[0].StartTime.Should().Be("03:30");
    }

    private static DateTimeOffset[] LisbonThursdaysBeforeTheTransition()
    {
        var first = new DateTimeOffset(2027, 1, 7, 3, 30, 0, TimeSpan.Zero);
        return Enumerable.Range(0, 9).Select(week => first.AddDays(7 * week)).ToArray();
    }

    private void StubLisbonSeries(params DateTimeOffset[] occurrences)
    {
        _api.ListCalendarsAsync(Token, Arg.Any<CancellationToken>())
            .Returns(new List<CalendarListEntry>
            {
                new() { Id = "primary", AccessRole = "owner", Summary = "Work" }
            });
        _api.ListEventsAsync(Token, "primary", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(occurrences
                .Select((start, index) => new Event
                {
                    Id = $"master-lisbon_{index}",
                    Summary = "Lisbon stand-up",
                    RecurringEventId = "master-lisbon",
                    Start = new EventDateTime { DateTimeDateTimeOffset = start },
                    End = new EventDateTime { DateTimeDateTimeOffset = start.AddMinutes(30) }
                })
                .ToList());
        _api.GetEventAsync(Token, "primary", "master-lisbon", Arg.Any<CancellationToken>())
            .Returns(new Event
            {
                Id = "master-lisbon",
                Summary = "Lisbon stand-up",
                Recurrence = ["RRULE:FREQ=DAILY;BYDAY=TH"],
                Start = new EventDateTime
                {
                    DateTimeDateTimeOffset = occurrences[0],
                    TimeZone = "Europe/Lisbon"
                }
            });
    }

    private User CreateUser(string timeZone)
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetTimeZone(timeZone).IsSuccess.Should().BeTrue();
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        _tokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns(Token);
        return user;
    }
}
