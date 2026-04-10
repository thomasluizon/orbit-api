using System.Text.Json;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Commands;

public record RunCalendarAutoSyncCommand(Guid UserId, bool IsOpportunistic = false)
    : IRequest<Result<CalendarAutoSyncResult>>;

public record CalendarAutoSyncResult(
    int NewSuggestions,
    int ReconciledHabits,
    GoogleCalendarAutoSyncStatus Status);

public partial class RunCalendarAutoSyncCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<GoogleCalendarSyncSuggestion> suggestionRepository,
    IGenericRepository<Notification> notificationRepository,
    IGoogleTokenService googleTokenService,
    ICalendarEventFetcher eventFetcher,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<RunCalendarAutoSyncCommandHandler> logger)
    : IRequestHandler<RunCalendarAutoSyncCommand, Result<CalendarAutoSyncResult>>
{
    private const int MaxSuggestionsPerTick = 25;
    private static readonly TimeSpan BackgroundDedupeWindow = TimeSpan.FromHours(4);
    private static readonly TimeSpan NotificationRateLimitWindow = TimeSpan.FromHours(24);
    private const int QuietHoursStart = 8;
    private const int QuietHoursEnd = 20;

    public async Task<Result<CalendarAutoSyncResult>> Handle(RunCalendarAutoSyncCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure<CalendarAutoSyncResult>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        if (!user.HasProAccess)
            return Result.Failure<CalendarAutoSyncResult>("Pro access required.", "calendar.autoSync.proRequired");

        if (!user.GoogleCalendarAutoSyncEnabled && !request.IsOpportunistic)
        {
            return Result.Success(new CalendarAutoSyncResult(0, 0, user.GoogleCalendarAutoSyncStatus ?? GoogleCalendarAutoSyncStatus.Idle));
        }

        if (user.GoogleAccessToken is null)
            return Result.Failure<CalendarAutoSyncResult>("Google Calendar not connected.", "calendar.autoSync.notConnected");

        if (!request.IsOpportunistic
            && user.GoogleCalendarLastSyncedAt is { } lastSync
            && utcNow - lastSync < BackgroundDedupeWindow)
        {
            return Result.Success(new CalendarAutoSyncResult(0, 0, user.GoogleCalendarAutoSyncStatus ?? GoogleCalendarAutoSyncStatus.Idle));
        }

        var refresh = await googleTokenService.TryRefreshAsync(user, cancellationToken);
        if (refresh.Result == GoogleTokenRefreshResult.RefreshTokenInvalid)
        {
            await HandleReconnectRequired(user, refresh.ErrorCode ?? "invalid_grant", cancellationToken);
            return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.ReconnectRequired));
        }

        if (refresh.Result == GoogleTokenRefreshResult.TransientFailure)
        {
            user.MarkCalendarSyncTransientError(refresh.ErrorCode ?? "refresh_failed");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.TransientError));
        }

        var accessToken = refresh.AccessToken ?? user.GoogleAccessToken;
        if (accessToken is null)
        {
            user.MarkCalendarSyncTransientError("no_access_token");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.TransientError));
        }

        return await FetchAndProcess(user, accessToken, utcNow, cancellationToken);
    }

    private async Task<Result<CalendarAutoSyncResult>> FetchAndProcess(
        User user, string accessToken, DateTime utcNow, CancellationToken ct)
    {
        List<CalendarEventItem> fetched;
        try
        {
            var service = CreateCalendarService(accessToken);
            fetched = await eventFetcher.FetchAsync(service, updatedMin: null, ct);
        }
        catch (Google.GoogleApiException ex)
        {
            LogGoogleApiError(logger, ex, user.Id);
            user.MarkCalendarSyncTransientError("google_api_error");
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.TransientError));
        }

        var reconciled = await ReconcileExistingHabits(user, fetched, utcNow, ct);
        var newSuggestions = await CreateSuggestions(user, fetched, utcNow, ct);

        if (newSuggestions > 0 && IsInQuietHours(user, utcNow)
            && !await HasRecentSuggestionNotification(user.Id, utcNow, ct))
        {
            await CreateSuggestionNotification(user.Id, newSuggestions, ct);
        }

        user.MarkCalendarSyncSuccess(utcNow);
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new CalendarAutoSyncResult(newSuggestions, reconciled, GoogleCalendarAutoSyncStatus.Idle));
    }

    private async Task<int> ReconcileExistingHabits(
        User user, List<CalendarEventItem> fetched, DateTime utcNow, CancellationToken ct)
    {
        if (user.GoogleCalendarSyncReconciledAt is not null)
            return 0;

        var eventsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ev in fetched)
        {
            var key = BuildLegacyMatchKey(ev.Title, ev.StartDate, ev.StartTime);
            eventsByKey.TryAdd(key, ev.Id);
        }

        var existingHabits = await habitRepository.FindTrackedAsync(
            h => h.UserId == user.Id && h.GoogleEventId == null, ct);

        int reconciled = 0;
        foreach (var habit in existingHabits)
        {
            var key = BuildLegacyMatchKey(
                habit.Title,
                habit.DueDate.ToString("yyyy-MM-dd"),
                habit.DueTime?.ToString("HH:mm"));
            if (eventsByKey.TryGetValue(key, out var googleEventId))
            {
                habit.SetGoogleEventId(googleEventId);
                reconciled++;
            }
        }

        user.MarkCalendarSyncReconciled(utcNow);
        return reconciled;
    }

    private async Task<int> CreateSuggestions(
        User user, List<CalendarEventItem> fetched, DateTime utcNow, CancellationToken ct)
    {
        if (fetched.Count == 0) return 0;

        var habitEventIds = (await habitRepository.FindAsync(
                h => h.UserId == user.Id && h.GoogleEventId != null, ct))
            .Select(h => h.GoogleEventId!)
            .ToHashSet(StringComparer.Ordinal);

        var existingSuggestionEventIds = (await suggestionRepository.FindAsync(
                s => s.UserId == user.Id && s.ImportedAtUtc == null && s.DismissedAtUtc == null, ct))
            .Select(s => s.GoogleEventId)
            .ToHashSet(StringComparer.Ordinal);

        int created = 0;
        foreach (var ev in fetched)
        {
            if (created >= MaxSuggestionsPerTick) break;
            if (habitEventIds.Contains(ev.Id)) continue;
            if (existingSuggestionEventIds.Contains(ev.Id)) continue;

            var startDateUtc = ParseStartDateUtc(ev);
            var rawJson = JsonSerializer.Serialize(ev);

            var suggestion = GoogleCalendarSyncSuggestion.Create(
                user.Id,
                ev.Id,
                ev.Title,
                startDateUtc,
                rawJson,
                utcNow);

            await suggestionRepository.AddAsync(suggestion, ct);
            created++;
        }

        return created;
    }

    private async Task HandleReconnectRequired(User user, string errorCode, CancellationToken ct)
    {
        user.MarkCalendarSyncReconnectRequired(errorCode);

        var notification = Notification.Create(
            user.Id,
            "Google Calendar disconnected",
            "Auto-sync paused. Reconnect to resume.",
            url: "/calendar-sync");
        await notificationRepository.AddAsync(notification, ct);

        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task<bool> HasRecentSuggestionNotification(Guid userId, DateTime utcNow, CancellationToken ct)
    {
        var cutoff = utcNow - NotificationRateLimitWindow;
        return await notificationRepository.AnyAsync(
            n => n.UserId == userId
                && n.Url == "/calendar-sync?mode=review"
                && n.CreatedAtUtc > cutoff,
            ct);
    }

    private async Task CreateSuggestionNotification(Guid userId, int count, CancellationToken ct)
    {
        var title = count == 1
            ? "1 new calendar event"
            : $"{count} new calendar events";
        var notification = Notification.Create(
            userId,
            title,
            "Tap to review and import",
            url: "/calendar-sync?mode=review");
        await notificationRepository.AddAsync(notification, ct);
    }

    private bool IsInQuietHours(User user, DateTime utcNow)
    {
        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        return local.Hour >= QuietHoursStart && local.Hour < QuietHoursEnd;
    }

    private static DateTime ParseStartDateUtc(CalendarEventItem ev)
    {
        if (DateTime.TryParse(ev.StartDate, out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return DateTime.UtcNow;
    }

    private static string BuildLegacyMatchKey(string title, string? startDate, string? startTime)
    {
        return $"{title.Trim().ToLowerInvariant()}|{startDate ?? ""}|{startTime ?? ""}";
    }

    private static CalendarService CreateCalendarService(string accessToken)
    {
        var credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromAccessToken(accessToken);
        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Orbit"
        });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Google API error during auto-sync for user {UserId}")]
    private static partial void LogGoogleApiError(ILogger logger, Exception ex, Guid userId);
}
