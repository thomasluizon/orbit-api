using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Notifications;
using Orbit.Application.Tests.Commands.Calendar;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Calendar;

/// <summary>
/// Drives the production write path (<see cref="RunCalendarAutoSyncCommandHandler"/>) into the
/// production read paths (<see cref="GetCalendarSyncSuggestionsQueryHandler"/> and
/// <see cref="GetCalendarEventsQueryHandler"/>) over one fetched event, so the stored suggestion row
/// under test is the row auto-sync really writes rather than a hand-built fixture.
/// </summary>
public class CalendarFeedAgreementTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo =
        Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGoogleTokenService _tokenService = Substitute.For<IGoogleTokenService>();
    private readonly ICalendarEventFetcher _fetcher = Substitute.For<ICalendarEventFetcher>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly FakeTimeProvider _timeProvider = new();

    private readonly RunCalendarAutoSyncCommandHandler _autoSync;
    private readonly GetCalendarSyncSuggestionsQueryHandler _suggestions;
    private readonly GetCalendarEventsQueryHandler _events;

    private readonly List<GoogleCalendarSyncSuggestion> _writtenSuggestions = [];

    public CalendarFeedAgreementTests()
    {
        _payGate.CanManageCalendar(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _payGate.CanAccessCalendar(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        _autoSync = new RunCalendarAutoSyncCommandHandler(
            new CalendarAutoSyncDependencies(
                _userRepo, _habitRepo, _suggestionRepo, _notificationRepo,
                _tokenService, _fetcher, _unitOfWork),
            _payGate,
            _timeProvider,
            Substitute.For<ILogger<RunCalendarAutoSyncCommandHandler>>());

        _suggestions = new GetCalendarSyncSuggestionsQueryHandler(
            _suggestionRepo, _habitRepo, _userRepo, _userDateService, _payGate,
            Substitute.For<ILogger<GetCalendarSyncSuggestionsQueryHandler>>());

        _events = new GetCalendarEventsQueryHandler(
            new GetCalendarEventsRepositories(_userRepo, _habitRepo, _suggestionRepo),
            _payGate, _tokenService, _fetcher, _unitOfWork,
            Substitute.For<ILogger<GetCalendarEventsQueryHandler>>());

        _timeProvider.SetUtcNow(new DateTime(2027, 1, 4, 12, 0, 0, DateTimeKind.Utc));

        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _notificationRepo.AnyAsync(Arg.Any<Expression<Func<Notification, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _suggestionRepo
            .When(repo => repo.AddAsync(
                Arg.Any<GoogleCalendarSyncSuggestion>(), Arg.Any<CancellationToken>()))
            .Do(call => _writtenSuggestions.Add(call.ArgAt<GoogleCalendarSyncSuggestion>(0)));
        _suggestionRepo.FindAsync(
                Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _writtenSuggestions.AsReadOnly());
        _suggestionRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _writtenSuggestions.AsReadOnly());
    }

    /// <summary>
    /// A <c>Europe/Lisbon</c> 03:30 <c>BYDAY=TH</c> series read by an <c>America/Sao_Paulo</c> account
    /// is Thursday 00:30 in January and Wednesday 23:30 from the Lisbon transition onward, so the
    /// weekday the rule names stops matching the day the account sees. Both feeds must withhold it,
    /// and the stored row must not resurrect it on the suggestion feed.
    /// </summary>
    [Fact]
    public async Task SeasonallyShiftingByDaySeries_IsWithheldByBothFeeds()
    {
        var user = CreateSyncingUser("America/Sao_Paulo");
        StubFetch(user, LisbonThursdayStandup());

        var eventsResult = await _events.Handle(new GetCalendarEventsQuery(user.Id), CancellationToken.None);
        eventsResult.IsSuccess.Should().BeTrue();
        eventsResult.Value.Should().BeEmpty("the events feed cannot encode this series honestly");

        var autoSyncResult = await _autoSync.Handle(new RunCalendarAutoSyncCommand(user.Id), CancellationToken.None);
        autoSyncResult.IsSuccess.Should().BeTrue();

        var suggestionsResult = await _suggestions.Handle(
            new GetCalendarSyncSuggestionsQuery(user.Id), CancellationToken.None);
        suggestionsResult.IsSuccess.Should().BeTrue();
        suggestionsResult.Value.Should().BeEmpty("the stored row carries the same series the events feed withheld");
    }

    /// <summary>
    /// The narrowing must not reach a series both zones agree on. <c>America/Sao_Paulo</c> and
    /// <c>America/Bogota</c> both hold one offset all year, so the account-local date of a
    /// mid-afternoon occurrence can never move.
    /// </summary>
    [Fact]
    public async Task StableByDaySeries_IsOfferedByBothFeeds()
    {
        var user = CreateSyncingUser("America/Bogota");
        StubFetch(user, SaoPauloAfternoonReview());

        var eventsResult = await _events.Handle(new GetCalendarEventsQuery(user.Id), CancellationToken.None);
        eventsResult.IsSuccess.Should().BeTrue();
        eventsResult.Value.Should().ContainSingle();
        eventsResult.Value[0].RecurrenceRule.Should().Be("RRULE:FREQ=DAILY;BYDAY=TH");

        var autoSyncResult = await _autoSync.Handle(new RunCalendarAutoSyncCommand(user.Id), CancellationToken.None);
        autoSyncResult.IsSuccess.Should().BeTrue();

        var suggestionsResult = await _suggestions.Handle(
            new GetCalendarSyncSuggestionsQuery(user.Id), CancellationToken.None);
        suggestionsResult.IsSuccess.Should().BeTrue();
        suggestionsResult.Value.Should().ContainSingle();
        suggestionsResult.Value[0].Event.RecurrenceRule.Should().Be("RRULE:FREQ=DAILY;BYDAY=TH");
        suggestionsResult.Value[0].Event.StartDate.Should().Be(eventsResult.Value[0].StartDate);
        suggestionsResult.Value[0].Event.StartTime.Should().Be(eventsResult.Value[0].StartTime);
    }

    [Theory]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=TH", "RRULE:FREQ=WEEKLY;BYDAY=WE")]
    [InlineData("RRULE:FREQ=DAILY;BYDAY=TH", "RRULE:FREQ=DAILY;BYDAY=WE")]
    public async Task UniformlyShiftedByDaySeries_IsOfferedByBothFeeds(string rule, string expectedRule)
    {
        var user = CreateSyncingUser("America/Sao_Paulo");
        var first = new DateTime(2027, 1, 7, 0, 0, 0, DateTimeKind.Utc);
        StubFetch(user, new CalendarEventItem(
            "master-tokyo", "Tokyo breakfast", null,
            "2027-01-07", "09:00", "10:00", true,
            rule, [],
            StartUtc: first, EndUtc: first.AddHours(1))
        {
            SourceTimeZone = "Asia/Tokyo",
            RecurrenceStartUtc = first,
            ExpandedOccurrencesUtc = [first, first.AddDays(7), first.AddDays(14)]
        });

        var eventsResult = await _events.Handle(new GetCalendarEventsQuery(user.Id), CancellationToken.None);
        eventsResult.IsSuccess.Should().BeTrue();
        eventsResult.Value.Should().ContainSingle();
        eventsResult.Value[0].StartDate.Should().Be("2027-01-06");
        eventsResult.Value[0].RecurrenceRule.Should().Be(expectedRule);

        var syncResult = await _autoSync.Handle(new RunCalendarAutoSyncCommand(user.Id), CancellationToken.None);
        syncResult.IsSuccess.Should().BeTrue();
        syncResult.Value.NewSuggestions.Should().Be(1);
        var suggestionsResult = await _suggestions.Handle(
            new GetCalendarSyncSuggestionsQuery(user.Id), CancellationToken.None);
        suggestionsResult.IsSuccess.Should().BeTrue();
        suggestionsResult.Value.Should().ContainSingle();
        suggestionsResult.Value[0].Event.StartDate.Should().Be(eventsResult.Value[0].StartDate);
        suggestionsResult.Value[0].Event.RecurrenceRule.Should().Be(eventsResult.Value[0].RecurrenceRule);
    }

    /// <summary>
    /// The read gate hides a withheld row, but it cannot undo the write. <c>newSuggestions</c> is the
    /// number <c>CreateSuggestionNotification</c> pushes, so a row auto-sync stores for a series the
    /// feed hides becomes a notification that opens an empty list. The write path must refuse it
    /// itself, and this asserts the row and the notification rather than the feed that follows them.
    /// </summary>
    [Fact]
    public async Task SeasonallyShiftingByDaySeries_IsNeitherStoredNorNotifiedByAutoSync()
    {
        var user = CreateSyncingUser("America/Sao_Paulo");
        StubFetch(user, LisbonThursdayStandup());

        var autoSyncResult = await _autoSync.Handle(new RunCalendarAutoSyncCommand(user.Id), CancellationToken.None);

        autoSyncResult.IsSuccess.Should().BeTrue();
        autoSyncResult.Value.NewSuggestions.Should().Be(0);
        _writtenSuggestions.Should().BeEmpty("the events feed cannot encode this series honestly");
        await _notificationRepo.DidNotReceive().AddAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The control for the test above, on the same account zone and the same clock, so the notification
    /// it asserts is absent is one this account really does receive for a series the gate allows.
    /// </summary>
    [Fact]
    public async Task StableByDaySeries_IsStoredAndNotifiedByAutoSync()
    {
        var user = CreateSyncingUser("America/Sao_Paulo");
        StubFetch(user, SaoPauloAfternoonReview());

        var autoSyncResult = await _autoSync.Handle(new RunCalendarAutoSyncCommand(user.Id), CancellationToken.None);

        autoSyncResult.IsSuccess.Should().BeTrue();
        autoSyncResult.Value.NewSuggestions.Should().Be(1);
        _writtenSuggestions.Should().ContainSingle();
        await _notificationRepo.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.Url == NotificationUrls.CalendarSyncReview),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LegacyPendingSuggestion_DoesNotHideLiveEventAndRefreshesOnAutoSync()
    {
        var user = CreateSyncingUser("America/Sao_Paulo");
        var fetched = SaoPauloAfternoonReview();
        StubFetch(user, fetched);
        var legacy = GoogleCalendarSyncSuggestion.Create(
            user.Id, fetched.Id, fetched.Title, fetched.StartUtc!.Value,
            JsonSerializer.Serialize(fetched),
            new DateTime(2027, 1, 4, 12, 0, 0, DateTimeKind.Utc));
        _writtenSuggestions.Add(legacy);

        var beforeRefresh = await _events.Handle(new GetCalendarEventsQuery(user.Id), CancellationToken.None);
        beforeRefresh.IsSuccess.Should().BeTrue();
        beforeRefresh.Value.Should().ContainSingle();

        var sync = await _autoSync.Handle(new RunCalendarAutoSyncCommand(user.Id), CancellationToken.None);
        sync.IsSuccess.Should().BeTrue();
        sync.Value.NewSuggestions.Should().Be(0);
        _writtenSuggestions.Should().ContainSingle();
        StoredCalendarEventJson.Deserialize(legacy.RawEventJson)!.SourceTimeZone
            .Should().Be("America/Sao_Paulo");
        var afterRefresh = await _suggestions.Handle(
            new GetCalendarSyncSuggestionsQuery(user.Id), CancellationToken.None);
        afterRefresh.IsSuccess.Should().BeTrue();
        afterRefresh.Value.Should().ContainSingle();
        var eventsAfterRefresh = await _events.Handle(new GetCalendarEventsQuery(user.Id), CancellationToken.None);
        eventsAfterRefresh.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyPendingSuggestion_WithDuplicateFetchedId_IsNotRefreshed()
    {
        var user = CreateSyncingUser("America/Sao_Paulo");
        var first = SaoPauloAfternoonReview();
        var second = first with { Title = "Another calendar's review", SourceTimeZone = "America/Bogota" };
        StubFetch(user, first);
        _fetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new List<CalendarEventItem> { first, second });
        var legacyJson = JsonSerializer.Serialize(first);
        var legacy = GoogleCalendarSyncSuggestion.Create(
            user.Id, first.Id, first.Title, first.StartUtc!.Value,
            legacyJson,
            new DateTime(2027, 1, 4, 12, 0, 0, DateTimeKind.Utc));
        _writtenSuggestions.Add(legacy);

        var sync = await _autoSync.Handle(new RunCalendarAutoSyncCommand(user.Id), CancellationToken.None);

        sync.IsSuccess.Should().BeTrue();
        sync.Value.NewSuggestions.Should().Be(0);
        _writtenSuggestions.Should().ContainSingle();
        legacy.RawEventJson.Should().Be(legacyJson);
        legacy.Title.Should().Be(first.Title);
    }

    private static CalendarEventItem LisbonThursdayStandup()
        => new(
            "master-lisbon",
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
            SourceTimeZone = "Europe/Lisbon",
            RecurrenceStartUtc = new DateTime(2027, 1, 7, 3, 30, 0, DateTimeKind.Utc)
        };

    private static CalendarEventItem SaoPauloAfternoonReview()
        => new(
            "master-sao-paulo",
            "Afternoon review",
            null,
            "2027-01-07",
            "15:00",
            "16:00",
            true,
            "RRULE:FREQ=DAILY;BYDAY=TH",
            [],
            StartUtc: new DateTime(2027, 1, 7, 18, 0, 0, DateTimeKind.Utc),
            EndUtc: new DateTime(2027, 1, 7, 19, 0, 0, DateTimeKind.Utc))
        {
            SourceTimeZone = "America/Sao_Paulo",
            RecurrenceStartUtc = new DateTime(2027, 1, 7, 18, 0, 0, DateTimeKind.Utc),
            ExpandedOccurrencesUtc = [new DateTime(2027, 1, 7, 18, 0, 0, DateTimeKind.Utc)]
        };

    private User CreateSyncingUser(string timeZone)
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeSubscription("sub_123", new DateTime(2027, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        user.SetGoogleTokens("access_old", "refresh_token");
        user.SetTimeZone(timeZone).IsSuccess.Should().BeTrue();
        user.EnableCalendarAutoSync();
        return user;
    }

    private void StubFetch(User user, CalendarEventItem item)
    {
        _userRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(user);
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2027, 1, 4));
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("new_access", GoogleTokenRefreshResult.Success, null));
        _tokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("new_access");
        _fetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new List<CalendarEventItem> { item });
    }
}
