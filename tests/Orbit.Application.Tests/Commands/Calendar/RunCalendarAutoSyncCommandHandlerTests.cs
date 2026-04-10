using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Calendar;

public class RunCalendarAutoSyncCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo = Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly IGoogleTokenService _tokenService = Substitute.For<IGoogleTokenService>();
    private readonly ICalendarEventFetcher _fetcher = Substitute.For<ICalendarEventFetcher>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ILogger<RunCalendarAutoSyncCommandHandler> _logger = Substitute.For<ILogger<RunCalendarAutoSyncCommandHandler>>();
    private readonly RunCalendarAutoSyncCommandHandler _handler;

    public RunCalendarAutoSyncCommandHandlerTests()
    {
        _handler = new RunCalendarAutoSyncCommandHandler(
            _userRepo, _habitRepo, _suggestionRepo, _notificationRepo,
            _tokenService, _fetcher, _unitOfWork, _timeProvider, _logger);

        // Default: mid-day so notifications pass quiet-hours check
        _timeProvider.SetUtcNow(new DateTime(2026, 4, 9, 14, 0, 0, DateTimeKind.Utc));

        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _suggestionRepo.FindAsync(Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());
        _notificationRepo.AnyAsync(Arg.Any<Expression<Func<Notification, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);
    }

    private static User CreateEnabledProUser(string timeZone = "UTC")
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(60));
        user.SetGoogleTokens("access_old", "refresh_token");
        user.SetTimeZone(timeZone);
        user.EnableCalendarAutoSync();
        return user;
    }

    private void StubUser(User user) =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        StubUser(null!);

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(Guid.NewGuid()), default);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NonProUser_ReturnsProRequired()
    {
        var user = User.Create("Test", "test@example.com").Value;
        // Clear trial to force non-pro
        typeof(User).GetProperty("TrialEndsAt")!.SetValue(user, null);
        StubUser(user);

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("calendar.autoSync.proRequired");
    }

    [Fact]
    public async Task Handle_RecentSync_ShortCircuits()
    {
        var user = CreateEnabledProUser();
        typeof(User).GetProperty("GoogleCalendarLastSyncedAt")!
            .SetValue(user, _timeProvider.GetUtcNow().UtcDateTime.AddHours(-1));
        StubUser(user);

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        result.IsSuccess.Should().BeTrue();
        await _tokenService.DidNotReceive().TryRefreshAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RefreshTokenInvalid_MarksReconnectRequiredAndCreatesNotification()
    {
        var user = CreateEnabledProUser();
        StubUser(user);
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome(null, GoogleTokenRefreshResult.RefreshTokenInvalid, "invalid_grant"));

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(GoogleCalendarAutoSyncStatus.ReconnectRequired);
        user.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
        user.GoogleCalendarAutoSyncStatus.Should().Be(GoogleCalendarAutoSyncStatus.ReconnectRequired);
        user.GoogleAccessToken.Should().BeNull();

        await _notificationRepo.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.Url == "/calendar-sync"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TransientFailure_SetsStatusAndReturns()
    {
        var user = CreateEnabledProUser();
        StubUser(user);
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome(null, GoogleTokenRefreshResult.TransientFailure, "timeout"));

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(GoogleCalendarAutoSyncStatus.TransientError);
        user.GoogleCalendarAutoSyncStatus.Should().Be(GoogleCalendarAutoSyncStatus.TransientError);
    }

    [Fact]
    public async Task Handle_Success_CreatesSuggestionsForNewEvents()
    {
        var user = CreateEnabledProUser();
        StubUser(user);
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("new_access", GoogleTokenRefreshResult.Success, null));

        _fetcher.FetchAsync(Arg.Any<Google.Apis.Calendar.v3.CalendarService>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>
            {
                new("evt_a", "Daily standup", null, "2026-04-10", "09:00", "09:30", true, null, []),
                new("evt_b", "Review", null, "2026-04-11", "10:00", "11:00", true, null, [])
            });

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.NewSuggestions.Should().Be(2);
        result.Value.Status.Should().Be(GoogleCalendarAutoSyncStatus.Idle);
        await _suggestionRepo.Received(2).AddAsync(
            Arg.Any<GoogleCalendarSyncSuggestion>(),
            Arg.Any<CancellationToken>());
        user.GoogleCalendarLastSyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_Success_DedupesAgainstExistingHabits()
    {
        var user = CreateEnabledProUser();
        StubUser(user);
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("new_access", GoogleTokenRefreshResult.Success, null));

        var existingHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Daily standup", FrequencyUnit.Day, 1,
            GoogleEventId: "evt_a")).Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { existingHabit }.AsReadOnly());

        _fetcher.FetchAsync(Arg.Any<Google.Apis.Calendar.v3.CalendarService>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>
            {
                new("evt_a", "Daily standup", null, "2026-04-10", "09:00", "09:30", true, null, []),
                new("evt_b", "Review", null, "2026-04-11", "10:00", "11:00", true, null, [])
            });

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        result.Value.NewSuggestions.Should().Be(1);
    }

    [Fact]
    public async Task Handle_Success_CapsAtMaxSuggestions()
    {
        var user = CreateEnabledProUser();
        StubUser(user);
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("new_access", GoogleTokenRefreshResult.Success, null));

        var many = Enumerable.Range(0, 40)
            .Select(i => new CalendarEventItem($"evt_{i}", $"Event {i}", null, "2026-04-10", null, null, false, null, []))
            .ToList();
        _fetcher.FetchAsync(Arg.Any<Google.Apis.Calendar.v3.CalendarService>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(many);

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        result.Value.NewSuggestions.Should().Be(25);
    }

    [Fact]
    public async Task Handle_Success_QuietHours_DoesNotCreateNotification()
    {
        // Set time to 03:00 UTC - outside quiet hours window of 08:00-20:00
        _timeProvider.SetUtcNow(new DateTime(2026, 4, 9, 3, 0, 0, DateTimeKind.Utc));

        var user = CreateEnabledProUser();
        StubUser(user);
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("new_access", GoogleTokenRefreshResult.Success, null));

        _fetcher.FetchAsync(Arg.Any<Google.Apis.Calendar.v3.CalendarService>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>
            {
                new("evt_a", "Event", null, "2026-04-10", null, null, false, null, [])
            });

        await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        await _notificationRepo.DidNotReceive().AddAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Success_RateLimit_DoesNotCreateNotificationIfRecentExists()
    {
        var user = CreateEnabledProUser();
        StubUser(user);
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("new_access", GoogleTokenRefreshResult.Success, null));
        _notificationRepo.AnyAsync(Arg.Any<Expression<Func<Notification, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _fetcher.FetchAsync(Arg.Any<Google.Apis.Calendar.v3.CalendarService>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>
            {
                new("evt_a", "Event", null, "2026-04-10", null, null, false, null, [])
            });

        await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        await _notificationRepo.DidNotReceive().AddAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Success_ReconciliationPass_BackfillsGoogleEventIdOnExistingHabit()
    {
        var user = CreateEnabledProUser();
        StubUser(user);
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("new_access", GoogleTokenRefreshResult.Success, null));

        var orphanHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Daily standup", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 4, 10),
            DueTime: new TimeOnly(9, 0))).Value;
        orphanHabit.GoogleEventId.Should().BeNull();

        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { orphanHabit }.AsReadOnly());

        _fetcher.FetchAsync(Arg.Any<Google.Apis.Calendar.v3.CalendarService>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>
            {
                new("evt_a", "Daily standup", null, "2026-04-10", "09:00", "09:30", true, null, [])
            });

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id), default);

        result.Value.ReconciledHabits.Should().Be(1);
        orphanHabit.GoogleEventId.Should().Be("evt_a");
        user.GoogleCalendarSyncReconciledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_OpportunisticSkipsDedupe()
    {
        var user = CreateEnabledProUser();
        typeof(User).GetProperty("GoogleCalendarLastSyncedAt")!
            .SetValue(user, _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5));
        StubUser(user);
        _tokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("new", GoogleTokenRefreshResult.Success, null));
        _fetcher.FetchAsync(Arg.Any<Google.Apis.Calendar.v3.CalendarService>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>());

        var result = await _handler.Handle(new RunCalendarAutoSyncCommand(user.Id, IsOpportunistic: true), default);

        result.IsSuccess.Should().BeTrue();
        await _tokenService.Received(1).TryRefreshAsync(user, Arg.Any<CancellationToken>());
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;
    public void SetUtcNow(DateTime utcNow) => _utcNow = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    public override DateTimeOffset GetUtcNow() => _utcNow;
}
