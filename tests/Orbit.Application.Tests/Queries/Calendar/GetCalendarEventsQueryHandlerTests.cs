using FluentAssertions;
using Orbit.Application.Calendar.Queries;
using NSubstitute;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Behaviors;

namespace Orbit.Application.Tests.Queries.Calendar;

public class GetCalendarEventsQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo = Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGoogleTokenService _googleTokenService = Substitute.For<IGoogleTokenService>();
    private readonly ICalendarEventFetcher _eventFetcher = Substitute.For<ICalendarEventFetcher>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly RecordingLogger _logger = new();
    private readonly GetCalendarEventsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetCalendarEventsQueryHandlerTests()
    {
        _payGate.CanAccessCalendar(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _handler = new GetCalendarEventsQueryHandler(
            new GetCalendarEventsRepositories(_userRepo, _habitRepo, _suggestionRepo), _payGate, _googleTokenService, _eventFetcher, _unitOfWork, _logger);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    private sealed class RecordingLogger : ILogger<GetCalendarEventsQueryHandler>
    {
        public List<(LogLevel Level, int EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
    }

    private void StubSuccessfulFetch(User user, params CalendarEventItem[] items)
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("valid-access-token");
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());
        _eventFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(items.ToList());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetCalendarEventsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound.Message);
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_NoGoogleToken_ReturnsFailure()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns((string?)null);

        var query = new GetCalendarEventsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.CalendarNotConnected.Message);
    }

    [Fact]
    public async Task Handle_UserNotFound_UsesCorrectErrorCode()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetCalendarEventsQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound.Message);
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_NoGoogleToken_DoesNotSaveChanges()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns((string?)null);

        var query = new GetCalendarEventsQuery(UserId);
        await _handler.Handle(query, CancellationToken.None);

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ValidToken_PersistsRefreshedToken()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("valid-access-token");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());
        _eventFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>());

        var query = new GetCalendarEventsQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_DoesNotCallTokenService()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetCalendarEventsQuery(UserId);
        await _handler.Handle(query, CancellationToken.None);

        await _googleTokenService.DidNotReceive()
            .GetValidAccessTokenAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoGoogleToken_ErrorMessageGuidesUser()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns((string?)null);

        var query = new GetCalendarEventsQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.Error.Should().Be(ErrorMessages.CalendarNotConnected.Message);
    }

    [Fact]
    public async Task Handle_FiltersOutAlreadyImportedHabitsByGoogleEventId()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("valid-access-token");

        var importedHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Existing", Domain.Enums.FrequencyUnit.Week, 1,
            DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            GoogleEventId: "evt_already")).Value;

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { importedHabit }.AsReadOnly());
        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());

        _eventFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>
            {
                new("evt_already", "Existing", null, "2026-05-01", null, null, true, null, []),
                new("evt_new", "Brand New", null, "2026-05-02", null, null, true, null, [])
            });

        var query = new GetCalendarEventsQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Id.Should().Be("evt_new");
    }

    [Fact]
    public async Task Handle_ShiftedMultiDayAlternateWeekRule_OmitsEvent()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_tokyo",
                "Tokyo breakfast",
                null,
                "2026-04-15",
                "08:00",
                "09:00",
                true,
                "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;WKST=SU",
                [],
                StartUtc: new DateTime(2026, 4, 14, 23, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc))
            {
                SourceTimeZone = "Asia/Tokyo",
                RecurrenceStartUtc = new DateTime(2026, 4, 14, 23, 0, 0, DateTimeKind.Utc),
                ExpandedOccurrencesUtc =
                [
                    new DateTime(2026, 4, 14, 23, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 4, 26, 23, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 4, 28, 23, 0, 0, DateTimeKind.Utc)
                ]
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ShiftedMultiDayWeeklyRule_UsesDailyDaysForInstalledImporter()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        var first = new DateTime(2027, 1, 7, 0, 0, 0, DateTimeKind.Utc);
        StubSuccessfulFetch(user, new CalendarEventItem(
            "evt_multiday_weekly", "Tokyo class", null,
            "2027-01-07", "09:00", "10:00", true,
            "RRULE:FREQ=WEEKLY;BYDAY=TH,SA;WKST=MO", [],
            StartUtc: first, EndUtc: first.AddHours(1))
        {
            SourceTimeZone = "Asia/Tokyo",
            RecurrenceStartUtc = first,
            ExpandedOccurrencesUtc = [first, first.AddDays(2), first.AddDays(7), first.AddDays(9)]
        });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2027-01-06");
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=DAILY;BYDAY=WE,FR");
    }

    [Fact]
    public async Task Handle_ShiftedAlternateWeekRuleWhoseAnchorIsNotNamed_OmitsEvent()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        var first = new DateTime(2027, 1, 7, 0, 0, 0, DateTimeKind.Utc);
        StubSuccessfulFetch(user, new CalendarEventItem(
            "evt_mismatched_anchor", "Tokyo class", null,
            "2027-01-07", "09:00", "10:00", true,
            "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO", [],
            StartUtc: first, EndUtc: first.AddHours(1))
        {
            SourceTimeZone = "Asia/Tokyo",
            RecurrenceStartUtc = first,
            ExpandedOccurrencesUtc = [first, first.AddDays(14)]
        });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ShiftedAlternateWeekRule_ShiftsTheDefaultWeekStart()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        var first = new DateTime(2026, 4, 14, 23, 0, 0, DateTimeKind.Utc);
        StubSuccessfulFetch(user, new CalendarEventItem(
            "evt_alternate", "Tokyo weekly review", null,
            "2026-04-15", "08:00", "09:00", true,
            "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=WE", [],
            StartUtc: first, EndUtc: first.AddHours(1))
        {
            SourceTimeZone = "Asia/Tokyo",
            RecurrenceStartUtc = first,
            ExpandedOccurrencesUtc = [first, first.AddDays(14)]
        });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=TU;WKST=SU");
    }

    [Theory]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=2TH")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=TH;BYSETPOS=1")]
    [InlineData("RRULE:FREQ=DAILY;BYDAY=TH;BYMONTHDAY=7")]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=TH")]
    [InlineData("RRULE:FREQ=DAILY;INTERVAL=2;BYDAY=TH")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=TH;BYMONTH=1")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=TH;BYYEARDAY=7")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=TH;BYWEEKNO=2")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=TH;COUNT=4")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=TH;UNTIL=20270131T000000Z")]
    public async Task Handle_ShiftedByDayRuleOutsideSupportedSubset_OmitsEvent(string rule)
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        var start = new DateTime(2027, 1, 7, 0, 0, 0, DateTimeKind.Utc);
        StubSuccessfulFetch(user, new CalendarEventItem(
            "evt_unsupported", "Tokyo breakfast", null,
            "2027-01-07", "09:00", "10:00", true, rule, [],
            StartUtc: start, EndUtc: start.AddHours(1))
        {
            SourceTimeZone = "Asia/Tokyo",
            RecurrenceStartUtc = start,
            ExpandedOccurrencesUtc = [start, start.AddDays(7)]
        });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ShiftedByDaySeriesWithSeasonalClockChange_OmitsEvent()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        var first = new DateTime(2027, 3, 4, 0, 30, 0, DateTimeKind.Utc);
        var later = new DateTime(2027, 3, 31, 23, 30, 0, DateTimeKind.Utc);
        StubSuccessfulFetch(user, new CalendarEventItem(
            "evt_lisbon_shift", "Lisbon midnight call", null,
            "2027-03-04", "00:30", "01:00", true,
            "RRULE:FREQ=WEEKLY;BYDAY=TH", [],
            StartUtc: first, EndUtc: first.AddMinutes(30))
        {
            SourceTimeZone = "Europe/Lisbon",
            RecurrenceStartUtc = first,
            ExpandedOccurrencesUtc = [first, later]
        });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ShiftedByDaySeriesDriftingAfterFetchWindow_OmitsEvent()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        var first = new DateTime(2027, 1, 7, 0, 30, 0, DateTimeKind.Utc);
        StubSuccessfulFetch(user, new CalendarEventItem(
            "evt_lisbon_later_drift", "Lisbon midnight call", null,
            "2027-01-07", "00:30", "01:00", true,
            "RRULE:FREQ=WEEKLY;BYDAY=TH", [],
            StartUtc: first, EndUtc: first.AddMinutes(30))
        {
            SourceTimeZone = "Europe/Lisbon",
            RecurrenceStartUtc = first,
            ExpandedOccurrencesUtc = [first, first.AddDays(7), first.AddDays(14)]
        });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_RecurringByDayEventOnSameLocalDateWhoseSeriesShiftsLater_OmitsEvent()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_lisbon_seasonal_shift",
                "Lisbon early meeting",
                null,
                "2026-01-15",
                "03:30",
                "04:30",
                true,
                "RRULE:FREQ=WEEKLY;BYDAY=TH",
                [],
                StartUtc: new DateTime(2026, 1, 15, 3, 30, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 1, 15, 4, 30, 0, DateTimeKind.Utc))
            {
                SourceTimeZone = "Europe/Lisbon"
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_RecurringEventWithoutByDay_KeepsRecurrenceRuleUnchanged()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_tokyo_without_byday",
                "Tokyo monthly meeting",
                null,
                "2026-04-15",
                "08:00",
                "09:00",
                true,
                "RRULE:FREQ=MONTHLY;BYMONTHDAY=15",
                [],
                StartUtc: new DateTime(2026, 4, 14, 23, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc))
            {
                SourceTimeZone = "Asia/Tokyo",
                RecurrenceStartUtc = new DateTime(2026, 4, 14, 23, 0, 0, DateTimeKind.Utc)
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=MONTHLY;BYMONTHDAY=15");
    }

    [Fact]
    public async Task Handle_ByDaySeriesWhoseSourceZoneOnlyShiftsOutsideTheFetchWindow_OmitsEvent()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_lisbon_daily",
                "Lisbon stand-up",
                null,
                "2027-01-07",
                "03:30",
                "04:00",
                true,
                "RRULE:FREQ=DAILY;BYDAY=TH",
                [],
                StartUtc: new DateTime(2027, 1, 7, 3, 30, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2027, 1, 7, 4, 0, 0, DateTimeKind.Utc))
            {
                SourceTimeZone = "Europe/Lisbon"
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ByDaySeriesNoOffsetChangeCanMove_KeepsRecurrenceRuleUnchanged()
    {
        var user = CreateTestUser();
        user.SetTimeZone("Europe/Lisbon").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_lisbon_afternoon",
                "Lisbon afternoon review",
                null,
                "2027-01-07",
                "15:00",
                "16:00",
                true,
                "RRULE:FREQ=DAILY;BYDAY=TH",
                [],
                StartUtc: new DateTime(2027, 1, 7, 15, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2027, 1, 7, 16, 0, 0, DateTimeKind.Utc))
            {
                SourceTimeZone = "Europe/Lisbon",
                RecurrenceStartUtc = new DateTime(2027, 1, 7, 15, 0, 0, DateTimeKind.Utc)
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2027-01-07");
        result.Value[0].StartTime.Should().Be("15:00");
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=DAILY;BYDAY=TH");
    }

    [Theory]
    [InlineData("RRULE:FREQ=DAILY;BYDAY=TH", 1, 7, 15, "12:00")]
    [InlineData("RRULE:FREQ=DAILY;BYDAY=TH", 7, 8, 14, "11:00")]
    [InlineData("RRULE:FREQ=DAILY", 1, 7, 15, "12:00")]
    [InlineData("RRULE:FREQ=DAILY", 7, 8, 14, "11:00")]
    public async Task Handle_RecurringLisbonAfternoonWithSeasonalClockDrift_OmitsEvent(
        string rule, int month, int day, int utcHour, string projectedTime)
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        var startUtc = new DateTime(2027, month, day, utcHour, 0, 0, DateTimeKind.Utc);
        var source = new CalendarEventItem(
            "evt_lisbon_afternoon_drift", "Lisbon afternoon", null,
            $"2027-{month:00}-{day:00}", "15:00", "16:00", true, rule, [],
            StartUtc: startUtc, EndUtc: startUtc.AddHours(1))
        {
            SourceTimeZone = "Europe/Lisbon",
            RecurrenceStartUtc = startUtc
        };
        source.ProjectTo(TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"))
            .StartTime.Should().Be(projectedTime);
        StubSuccessfulFetch(user, source);

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_RescheduledFirstInstance_UsesOriginalRecurrenceClock()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        var actualStart = new DateTime(2027, 1, 7, 15, 0, 0, DateTimeKind.Utc);
        var originalStart = new DateTime(2027, 1, 7, 3, 30, 0, DateTimeKind.Utc);
        StubSuccessfulFetch(user, new CalendarEventItem(
            "evt_moved_first", "Moved Lisbon stand-up", null,
            "2027-01-07", "15:00", "16:00", true,
            "RRULE:FREQ=DAILY;BYDAY=TH", [],
            StartUtc: actualStart, EndUtc: actualStart.AddHours(1))
        {
            SourceTimeZone = "Europe/Lisbon",
            RecurrenceStartUtc = originalStart
        });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ByDaySeriesBetweenTwoZonesWithoutSeasonalDisagreement_KeepsRecurrenceRuleUnchanged()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Bogota").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_sao_paulo_review",
                "Afternoon review",
                null,
                "2027-01-07",
                "15:00",
                "16:00",
                true,
                "RRULE:FREQ=WEEKLY;BYDAY=TH",
                [],
                StartUtc: new DateTime(2027, 1, 7, 18, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2027, 1, 7, 19, 0, 0, DateTimeKind.Utc))
            {
                SourceTimeZone = "America/Sao_Paulo",
                RecurrenceStartUtc = new DateTime(2027, 1, 7, 18, 0, 0, DateTimeKind.Utc)
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2027-01-07");
        result.Value[0].StartTime.Should().Be("13:00");
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=WEEKLY;BYDAY=TH");
    }

    [Fact]
    public async Task Handle_ByDaySeriesWithoutASourceTimeZone_OmitsEvent()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_unknown_source_zone",
                "Legacy stand-up",
                null,
                "2027-01-07",
                "15:00",
                "16:00",
                true,
                "RRULE:FREQ=WEEKLY;BYDAY=TH",
                [],
                StartUtc: new DateTime(2027, 1, 7, 18, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2027, 1, 7, 19, 0, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    /// <summary>
    /// The account reads a calendar kept in its own zone, so the projection is the identity and no
    /// occurrence can move. One date of the year loses the wall clock to a spring-forward gap, and on
    /// that date the series produces no occurrence at all, so the date is not evidence against it.
    /// </summary>
    [Theory]
    [InlineData("America/New_York", "02:30")]
    [InlineData("America/Los_Angeles", "02:30")]
    [InlineData("America/Chicago", "02:30")]
    [InlineData("Europe/Berlin", "02:30")]
    [InlineData("Europe/London", "01:30")]
    [InlineData("Australia/Sydney", "02:30")]
    [InlineData("Pacific/Auckland", "02:30")]
    public async Task Handle_ByDaySeriesOnAWallClockOneGapRemoves_IsKeptWhenTheAccountReadsItsOwnZone(
        string zoneId, string wallClock)
    {
        var user = CreateTestUser();
        user.SetTimeZone(zoneId).IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_own_zone_gap",
                "Night shift",
                null,
                "2027-01-07",
                wallClock,
                null,
                true,
                "RRULE:FREQ=WEEKLY;BYDAY=TH",
                [],
                StartUtc: ToUtc(zoneId, "2027-01-07", wallClock))
            {
                SourceTimeZone = zoneId,
                RecurrenceStartUtc = ToUtc(zoneId, "2027-01-07", wallClock)
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2027-01-07");
        result.Value[0].StartTime.Should().Be(wallClock);
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=WEEKLY;BYDAY=TH");
    }

    /// <summary>
    /// <c>America/New_York</c> and <c>America/Chicago</c> hold different rules, so the walk really
    /// runs, and they stay exactly one hour apart all year, so the account-local date can never move.
    /// The source zone still loses 02:30 to its own spring-forward gap on 2027-03-14, and the series
    /// must survive that date.
    /// </summary>
    [Fact]
    public async Task Handle_ByDaySeriesOnAWallClockTheSourceGapRemoves_IsKeptWhenTheDateCannotMove()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Chicago").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_new_york_gap",
                "New York night shift",
                null,
                "2027-01-07",
                "02:30",
                null,
                true,
                "RRULE:FREQ=WEEKLY;BYDAY=TH",
                [],
                StartUtc: ToUtc("America/New_York", "2027-01-07", "02:30"))
            {
                SourceTimeZone = "America/New_York",
                RecurrenceStartUtc = ToUtc("America/New_York", "2027-01-07", "02:30")
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2027-01-07");
        result.Value[0].StartTime.Should().Be("01:30");
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=WEEKLY;BYDAY=TH");
    }

    /// <summary>
    /// Lebanon ends its summer time at 00:00 local, so 23:00 to 23:59 on that Saturday happens twice
    /// in <c>Asia/Beirut</c>. The earlier instant is 23:30 in <c>Europe/Athens</c> and the later one is
    /// 00:30 the next day, because Athens keeps its own offset for three more hours. Every other date
    /// of the year holds the two zones on the same clock, so the repeated hour is the only reason this
    /// series can show the account a different weekday, and it is enough to withhold it.
    /// </summary>
    /// <remarks>
    /// The series starts the Friday after Lebanon's 2026 transition, so the repeated hour it fails on
    /// is 2027-10-30, exactly 365 days later. That also pins the length of the walk: a
    /// <c>ProbeDays</c> of 364 or less never reaches the only date that decides this series.
    /// </remarks>
    [Fact]
    public async Task Handle_ByDaySeriesInsideARepeatedHourWhoseTwoInstantsDisagree_OmitsEvent()
    {
        var user = CreateTestUser();
        user.SetTimeZone("Europe/Athens").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_beirut_late",
                "Beirut late call",
                null,
                "2026-10-30",
                "23:30",
                null,
                true,
                "RRULE:FREQ=WEEKLY;BYDAY=FR",
                [],
                StartUtc: ToUtc("Asia/Beirut", "2026-10-30", "23:30"))
            {
                SourceTimeZone = "Asia/Beirut"
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    /// <summary>
    /// An all-day event carries no <see cref="CalendarEventItem.EndTime"/>, so the projection drops
    /// nothing and the omission log stays silent. Without the <c>EndTime is not null</c> half of the
    /// guard the Debug line fires for every all-day event on every calendar read.
    /// </summary>
    [Fact]
    public async Task Handle_AllDayEvent_LogsNoOmittedEndTime()
    {
        var user = CreateTestUser();
        user.SetTimeZone("Asia/Tokyo").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_all_day_quiet",
                "Holiday",
                null,
                "2026-04-15",
                null,
                null,
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].EndTime.Should().BeNull();
        _logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Debug);
    }

    /// <summary>
    /// Builds the instant a source calendar means by one wall clock, so a test states the zone and the
    /// clock the user sees rather than a UTC value whose offset a reader has to verify by hand.
    /// </summary>
    private static DateTime ToUtc(string zoneId, string date, string wallClock)
    {
        var local = DateTime.ParseExact(
            $"{date} {wallClock}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None);
        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById(zoneId));
    }

    [Fact]
    public async Task Handle_AllDayByDaySeries_KeepsItsFloatingDateAndRule()
    {
        var user = CreateTestUser();
        user.SetTimeZone("Asia/Tokyo").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_all_day_series",
                "Weekly holiday",
                null,
                "2027-01-07",
                null,
                null,
                true,
                "RRULE:FREQ=WEEKLY;BYDAY=TH",
                [],
                StartUtc: new DateTime(2027, 1, 7, 0, 0, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2027-01-07");
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=WEEKLY;BYDAY=TH");
    }

    [Fact]
    public async Task Handle_EventCrossingLocalMidnight_OmitsEndTimeKeepsEndUtcAndLogsTheReason()
    {
        var user = CreateTestUser();
        user.SetTimeZone("Asia/Kathmandu").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_kathmandu_late",
                "Late review",
                null,
                "2026-09-20",
                "18:05",
                "18:35",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 9, 20, 18, 5, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 9, 20, 18, 35, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2026-09-20");
        result.Value[0].StartTime.Should().Be("23:50");
        result.Value[0].EndTime.Should().BeNull();
        result.Value[0].EndUtc.Should().Be(new DateTime(2026, 9, 20, 18, 35, 0, DateTimeKind.Utc));
        _logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("end time", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handle_EventInsideRepeatedHour_OmitsEndTimeKeepsEndUtcAndLogsTheReason()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/New_York").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_new_york_fold",
                "Night shift handover",
                null,
                "2026-11-01",
                "01:30",
                "02:15",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 11, 1, 6, 15, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2026-11-01");
        result.Value[0].StartTime.Should().Be("01:30");
        result.Value[0].EndTime.Should().BeNull();
        result.Value[0].EndUtc.Should().Be(new DateTime(2026, 11, 1, 6, 15, 0, DateTimeKind.Utc));
        _logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("end time", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handle_EventKeepingItsLocalDate_LogsNoOmittedEndTime()
    {
        var user = CreateTestUser();
        user.SetTimeZone("Asia/Kathmandu").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_kathmandu_morning",
                "Morning review",
                null,
                "2026-09-20",
                "03:00",
                "03:30",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 9, 20, 3, 30, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value[0].StartTime.Should().Be("08:45");
        result.Value[0].EndTime.Should().Be("09:15");
        _logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Debug);
    }

    [Theory]
    [InlineData("Asia/Kathmandu", "2026-04-15", "04:45", "05:45")]
    [InlineData("Pacific/Chatham", "2026-04-15", "11:45", "12:45")]
    public async Task Handle_SubHourOffsetTimezone_ProjectsStartAndEndToTheQuarterHour(
        string timeZone, string expectedDate, string expectedStart, string expectedEnd)
    {
        var user = CreateTestUser();
        user.SetTimeZone(timeZone).IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_tokyo_breakfast",
                "Tokyo breakfast",
                null,
                "2026-04-15",
                "08:00",
                "09:00",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 4, 14, 23, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be(expectedDate);
        result.Value[0].StartTime.Should().Be(expectedStart);
        result.Value[0].EndTime.Should().Be(expectedEnd);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("Not/AZone")]
    [InlineData("America/Sao_Paulo ")]
    public async Task Handle_StoredTimeZoneTheSystemCannotResolve_ProjectsIntoUtcAndLogsAWarning(string storedTimeZone)
    {
        var user = CreateTestUser();
        ForceStoredTimeZone(user, storedTimeZone);
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_unresolvable_zone",
                "Team sync",
                null,
                "2026-09-20",
                "18:05",
                "18:35",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 9, 20, 18, 5, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 9, 20, 18, 35, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value[0].StartTime.Should().Be("18:05");
        _logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message == $"Unusable timezone {storedTimeZone} for user {UserId}, falling back to UTC");
    }

    /// <summary>
    /// Writes the column the way EF materializes it, around <see cref="User.SetTimeZone"/>. That is the
    /// only way a row reaches the reader with an id the system cannot resolve, and it is exactly what a
    /// row written before the boundary guard looks like.
    /// </summary>
    private static void ForceStoredTimeZone(User user, string timeZone)
    {
        typeof(User)
            .GetProperty(nameof(User.TimeZone))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(user, [timeZone]);
    }

    [Fact]
    public async Task Handle_TimedEventWithoutEndUtc_ProjectsStartAndOmitsEndTime()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_legacy",
                "Legacy Tokyo breakfast",
                null,
                "2026-04-15",
                "08:00",
                "09:00",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 4, 14, 23, 0, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2026-04-14");
        result.Value[0].StartTime.Should().Be("20:00");
        result.Value[0].EndTime.Should().BeNull();
        _logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("end time", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handle_TimedSaoPauloEvent_ProjectsIntoNextTokyoDay()
    {
        var user = CreateTestUser();
        user.SetTimeZone("Asia/Tokyo").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_sao_paulo",
                "Late dinner",
                null,
                "2026-04-15",
                "23:00",
                "00:00",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 4, 16, 2, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 4, 16, 3, 0, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2026-04-16");
        result.Value[0].StartTime.Should().Be("11:00");
        result.Value[0].EndTime.Should().Be("12:00");
    }

    [Theory]
    [InlineData("America/Sao_Paulo")]
    [InlineData("Asia/Tokyo")]
    public async Task Handle_AllDayEvent_PreservesFloatingDateAndNullTimes(string timeZone)
    {
        var user = CreateTestUser();
        user.SetTimeZone(timeZone).IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_all_day",
                "Holiday",
                null,
                "2026-04-15",
                null,
                null,
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2026-04-15");
        result.Value[0].StartTime.Should().BeNull();
        result.Value[0].EndTime.Should().BeNull();
    }

    [Fact]
    public async Task Handle_UserWithoutTimezone_OmitsEndTimeWhenProjectionCrossesUtcMidnight()
    {
        var user = CreateTestUser();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_utc_fallback",
                "Tokyo breakfast",
                null,
                "2026-04-15",
                "08:00",
                "09:00",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 4, 14, 23, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2026-04-14");
        result.Value[0].StartTime.Should().Be("23:00");
        result.Value[0].EndTime.Should().BeNull();
    }

    [Fact]
    public async Task Handle_TimedEventWithinDisplayedMinute_OmitsEqualEndTime()
    {
        var user = CreateTestUser();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_same_minute",
                "Short event",
                null,
                "2026-04-15",
                "10:00",
                "10:00",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 4, 15, 10, 0, 10, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 4, 15, 10, 0, 50, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2026-04-15");
        result.Value[0].StartTime.Should().Be("10:00");
        result.Value[0].EndTime.Should().BeNull();
    }

    [Fact]
    public async Task Handle_OneMinuteTimedEvent_KeepsLaterEndTime()
    {
        var user = CreateTestUser();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_one_minute",
                "One minute event",
                null,
                "2026-04-15",
                "10:00",
                "10:01",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 4, 15, 10, 1, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2026-04-15");
        result.Value[0].StartTime.Should().Be("10:00");
        result.Value[0].EndTime.Should().Be("10:01");
    }

    [Fact]
    public async Task Handle_TimedEventCrossingRepeatedHour_OmitsDescendingEndTime()
    {
        var user = CreateTestUser();
        user.SetTimeZone("America/New_York").IsSuccess.Should().BeTrue();
        StubSuccessfulFetch(
            user,
            new CalendarEventItem(
                "evt_fall_back",
                "Repeated hour",
                null,
                "2026-11-01",
                "01:30",
                "01:15",
                false,
                null,
                [],
                StartUtc: new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc),
                EndUtc: new DateTime(2026, 11, 1, 6, 15, 0, DateTimeKind.Utc)));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2026-11-01");
        result.Value[0].StartTime.Should().Be("01:30");
        result.Value[0].EndTime.Should().BeNull();
    }

    [Fact]
    public async Task Handle_InvalidRefreshToken_MarksReconnectRequiredAndReturnsConnectionFailure()
    {
        var user = CreateTestUser();
        user.SetGoogleTokens("expired-access-token", "refresh-token");

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome(null, GoogleTokenRefreshResult.RefreshTokenInvalid, "invalid_grant"));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.CalendarNotConnected.Message);
        user.GoogleAccessToken.Should().BeNull();
        user.GoogleRefreshToken.Should().BeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GoogleApiAuthenticationError_MarksReconnectRequiredAndReturnsReconnectMessage()
    {
        var user = CreateTestUser();
        user.SetGoogleTokens("stale-access-token", null);

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("stale-access-token");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());
        _eventFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<List<CalendarEventItem>>>(_ => throw new CalendarProviderException(
                CalendarFetchErrorKind.ReconnectRequired,
                "Invalid authentication credentials",
                "reconnect required",
                new InvalidOperationException("stub")));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.CalendarReconnectRequired.Message);
        user.GoogleAccessToken.Should().BeNull();
        user.GoogleRefreshToken.Should().BeNull();
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GoogleApiTransientError_ReturnsFetchFailedWithoutMarkingReconnect()
    {
        var user = CreateTestUser();
        user.SetGoogleTokens("access-token", null);

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("access-token");
        _eventFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<List<CalendarEventItem>>>(_ => throw new CalendarProviderException(
                CalendarFetchErrorKind.Transient,
                "backendError",
                "transient failure",
                new InvalidOperationException("stub")));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.CalendarFetchFailed.Message);
        user.GoogleAccessToken.Should().Be("access-token");
        user.GoogleRefreshToken.Should().BeNull();
    }

    [Fact]
    public async Task Handle_TokenWriteConcurrencyConflict_AbsorbedByRetryBehavior()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("valid-access-token");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _suggestionRepo.FindAsync(Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());
        _eventFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>());
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DbUpdateConcurrencyException("conflict"), _ => 0);

        var behavior = new ConcurrencyRetryBehavior<GetCalendarEventsQuery, Result<List<CalendarEventItem>>>(_unitOfWork);
        var query = new GetCalendarEventsQuery(UserId);

        var result = await behavior.Handle(query, ct => _handler.Handle(query, ct), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CalendarEventItem_Properties_SetCorrectly()
    {
        var item = new CalendarEventItem(
            "evt_123", "Team Meeting", "Weekly sync",
            "2026-04-03", "14:00", "15:00",
            true, "RRULE:FREQ=WEEKLY;BYDAY=FR",
            [15, 30]);

        item.Id.Should().Be("evt_123");
        item.Title.Should().Be("Team Meeting");
        item.Description.Should().Be("Weekly sync");
        item.StartDate.Should().Be("2026-04-03");
        item.StartTime.Should().Be("14:00");
        item.EndTime.Should().Be("15:00");
        item.IsRecurring.Should().BeTrue();
        item.RecurrenceRule.Should().Be("RRULE:FREQ=WEEKLY;BYDAY=FR");
        item.Reminders.Should().Equal(15, 30);
    }

    [Fact]
    public void GetCalendarEventsQuery_RecordEquality()
    {
        var id = Guid.NewGuid();
        var q1 = new GetCalendarEventsQuery(id);
        var q2 = new GetCalendarEventsQuery(id);

        q1.Should().Be(q2);
        q1.UserId.Should().Be(id);
    }

}
