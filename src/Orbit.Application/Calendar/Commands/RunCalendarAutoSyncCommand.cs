using System.Text.Json;
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

public record CalendarAutoSyncDependencies(
    IGenericRepository<User> UserRepository,
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<GoogleCalendarSyncSuggestion> SuggestionRepository,
    IGenericRepository<Notification> NotificationRepository,
    IGoogleTokenService GoogleTokenService,
    ICalendarEventFetcher EventFetcher,
    IUnitOfWork UnitOfWork);

public partial class RunCalendarAutoSyncCommandHandler(
    CalendarAutoSyncDependencies deps,
    IPayGateService payGate,
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
        var gateCheck = await payGate.CanManageCalendar(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<CalendarAutoSyncResult>();

        var user = await deps.UserRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure<CalendarAutoSyncResult>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

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

        var refresh = await deps.GoogleTokenService.TryRefreshAsync(user, cancellationToken);
        if (refresh.Result == GoogleTokenRefreshResult.RefreshTokenInvalid)
        {
            await HandleReconnectRequired(user, refresh.ErrorCode ?? "invalid_grant", cancellationToken);
            return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.ReconnectRequired));
        }

        if (refresh.Result == GoogleTokenRefreshResult.TransientFailure)
        {
            user.MarkCalendarSyncTransientError(refresh.ErrorCode ?? "refresh_failed");
            await deps.UnitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.TransientError));
        }

        var accessToken = refresh.AccessToken ?? user.GoogleAccessToken;
        if (accessToken is null)
        {
            user.MarkCalendarSyncTransientError("no_access_token");
            await deps.UnitOfWork.SaveChangesAsync(cancellationToken);
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
            fetched = await deps.EventFetcher.FetchAsync(accessToken, updatedMin: null, ct);
            fetched = NormalizeFetchedEvents(user.Id, fetched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Google SDK exceptions surface as opaque errors to Application (Infrastructure
            // owns the SDK and catches Google.GoogleApiException internally if it wants to
            // classify). Any thrown exception here is treated as a transient provider error.
            LogGoogleApiError(logger, ex, user.Id);
            user.MarkCalendarSyncTransientError("google_api_error");
            await deps.UnitOfWork.SaveChangesAsync(ct);
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
        await deps.UnitOfWork.SaveChangesAsync(ct);

        return Result.Success(new CalendarAutoSyncResult(newSuggestions, reconciled, GoogleCalendarAutoSyncStatus.Idle));
    }

    private async Task<int> ReconcileExistingHabits(
        User user, List<CalendarEventItem> fetched, DateTime utcNow, CancellationToken ct)
    {
        var assignedEventIds = (await deps.HabitRepository.FindAsync(
                h => h.UserId == user.Id && h.GoogleEventId != null, ct))
            .Select(h => h.GoogleEventId!)
            .ToHashSet(StringComparer.Ordinal);
        var reservedEventIds = new HashSet<string>(assignedEventIds, StringComparer.Ordinal);

        var eventsByKey = fetched
            .Where(ev => !assignedEventIds.Contains(ev.Id))
            .GroupBy(ev => BuildLegacyMatchKey(ev.Title, ev.StartDate, ev.StartTime), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Id, StringComparer.Ordinal);

        if (eventsByKey.Count == 0)
        {
            user.MarkCalendarSyncReconciled(utcNow);
            return 0;
        }

        var existingHabits = await deps.HabitRepository.FindTrackedAsync(
            h => h.UserId == user.Id && h.GoogleEventId == null, ct);

        var habitsByKey = existingHabits
            .GroupBy(habit => BuildLegacyMatchKey(
                habit.Title,
                habit.DueDate.ToString("yyyy-MM-dd"),
                habit.DueTime?.ToString("HH:mm")), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        int reconciled = 0;
        foreach (var (key, habit) in habitsByKey)
        {
            if (!eventsByKey.TryGetValue(key, out var googleEventId))
                continue;

            if (!reservedEventIds.Add(googleEventId))
            {
                LogDuplicateReconciliationEventId(logger, user.Id, habit.Id, googleEventId);
                continue;
            }

            habit.SetGoogleEventId(googleEventId);
            reconciled++;
        }

        user.MarkCalendarSyncReconciled(utcNow);
        return reconciled;
    }

    private async Task<int> CreateSuggestions(
        User user, List<CalendarEventItem> fetched, DateTime utcNow, CancellationToken ct)
    {
        if (fetched.Count == 0) return 0;

        var habitEventIds = (await deps.HabitRepository.FindAsync(
                h => h.UserId == user.Id && h.GoogleEventId != null, ct))
            .Select(h => h.GoogleEventId!)
            .ToHashSet(StringComparer.Ordinal);

        var existingSuggestionEventIds = (await deps.SuggestionRepository.FindAsync(
                s => s.UserId == user.Id && s.ImportedAtUtc == null && s.DismissedAtUtc == null, ct))
            .Select(s => s.GoogleEventId)
            .ToHashSet(StringComparer.Ordinal);
        var reservedEventIds = new HashSet<string>(habitEventIds, StringComparer.Ordinal);
        reservedEventIds.UnionWith(existingSuggestionEventIds);

        int created = 0;
        foreach (var ev in fetched)
        {
            if (created >= MaxSuggestionsPerTick) break;
            if (!reservedEventIds.Add(ev.Id)) continue;

            var startDateUtc = ParseStartDateUtc(ev);
            var rawJson = JsonSerializer.Serialize(ev);

            var suggestion = GoogleCalendarSyncSuggestion.Create(
                user.Id,
                ev.Id,
                ev.Title,
                startDateUtc,
                rawJson,
                utcNow);

            await deps.SuggestionRepository.AddAsync(suggestion, ct);
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
        await deps.NotificationRepository.AddAsync(notification, ct);

        await deps.UnitOfWork.SaveChangesAsync(ct);
    }

    private async Task<bool> HasRecentSuggestionNotification(Guid userId, DateTime utcNow, CancellationToken ct)
    {
        var cutoff = utcNow - NotificationRateLimitWindow;
        return await deps.NotificationRepository.AnyAsync(
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
        await deps.NotificationRepository.AddAsync(notification, ct);
    }

    private bool IsInQuietHours(User user, DateTime utcNow)
    {
        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        return local.Hour >= QuietHoursStart && local.Hour < QuietHoursEnd;
    }

    private static DateTime ParseStartDateUtc(CalendarEventItem ev)
    {
        if (DateTime.TryParse(ev.StartDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return DateTime.UtcNow;
    }

    private static string BuildLegacyMatchKey(string title, string? startDate, string? startTime)
    {
        return $"{title.Trim().ToLowerInvariant()}|{startDate ?? ""}|{startTime ?? ""}";
    }

    private List<CalendarEventItem> NormalizeFetchedEvents(Guid userId, List<CalendarEventItem> fetched)
    {
        if (fetched.Count <= 1)
            return fetched;

        var unique = new List<CalendarEventItem>(fetched.Count);
        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ev in fetched)
        {
            if (!seenEventIds.Add(ev.Id))
            {
                LogDuplicateFetchedEventId(logger, userId, ev.Id);
                continue;
            }

            unique.Add(ev);
        }

        return unique;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Google API error during auto-sync for user {UserId}")]
    private static partial void LogGoogleApiError(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Duplicate Google Calendar event id skipped during auto-sync for user {UserId}: {GoogleEventId}")]
    private static partial void LogDuplicateFetchedEventId(ILogger logger, Guid userId, string googleEventId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Duplicate Google Calendar event id reconciliation skipped for user {UserId}, habit {HabitId}: {GoogleEventId}")]
    private static partial void LogDuplicateReconciliationEventId(ILogger logger, Guid userId, Guid habitId, string googleEventId);
}
