using FluentAssertions;
using Orbit.Application.Calendar.Queries;
using NSubstitute;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
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
        result.Error.Should().Contain("User not found");
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
        result.Error.Should().Contain("Google Calendar not connected");
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

        result.Error.Should().Contain("sign in with Google");
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
    public async Task Handle_RecurringByDayEventCrossingAccountDate_OmitsEvent()
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
                SourceTimeZone = "Asia/Tokyo"
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
                SourceTimeZone = "Asia/Tokyo"
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
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
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
                SourceTimeZone = "Europe/Lisbon"
            });

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].StartDate.Should().Be("2027-01-07");
        result.Value[0].StartTime.Should().Be("12:00");
        result.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=DAILY;BYDAY=TH");
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
                SourceTimeZone = "America/Sao_Paulo"
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
        result.Error.Should().Contain("Google Calendar not connected");
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
        result.Error.Should().Be("Google Calendar connection expired. Please reconnect.");
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
