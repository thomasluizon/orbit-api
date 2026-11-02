using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Behaviors;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Application.Notifications;
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
    GoogleCalendarAutoSyncStatus Status,
    bool AlreadySyncing = false);

public record CalendarAutoSyncDependencies(
    IGenericRepository<User> UserRepository,
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<GoogleCalendarSyncSuggestion> SuggestionRepository,
    IGenericRepository<Notification> NotificationRepository,
    IGoogleTokenService GoogleTokenService,
    ICalendarEventFetcher EventFetcher,
    IUnitOfWork UnitOfWork,
    ICalendarSyncLock SyncLock);

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
        var preparation = await ConcurrencyRetry.RunAsync(deps.UnitOfWork, ct => PrepareAsync(request, ct), cancellationToken);
        if (preparation.IsFailure) return preparation.PropagateError<CalendarAutoSyncResult>();
        var user = preparation.Value.User;
        if (!preparation.Value.ShouldSync)
            return Result.Success(new CalendarAutoSyncResult(0, 0, user.GoogleCalendarAutoSyncStatus ?? GoogleCalendarAutoSyncStatus.Idle));
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var refresh = user.GoogleAccessTokenExpiresAtUtc > utcNow.AddMinutes(1)
            ? new GoogleTokenRefreshOutcome(user.GoogleAccessToken, GoogleTokenRefreshResult.Success, null)
            : await deps.GoogleTokenService.TryRefreshAsync(user, cancellationToken);
        if (refresh.Result == GoogleTokenRefreshResult.RefreshTokenInvalid
            || refresh.ErrorCode == GoogleTokenErrorCodes.NoRefreshToken)
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

        return await FetchAndProcess(user, accessToken, request, utcNow, cancellationToken);
    }

    private sealed record SyncPreparation(User User, bool ShouldSync);

    private async Task<Result<SyncPreparation>> PrepareAsync(RunCalendarAutoSyncCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanManageCalendar(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<SyncPreparation>();

        var user = await deps.UserRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure<SyncPreparation>(ErrorMessages.UserNotFound);

        await deps.UserRepository.ReloadAsync(user, cancellationToken);

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        if (!user.GoogleCalendarAutoSyncEnabled && !request.IsOpportunistic)
        {
            return Result.Success(new SyncPreparation(user, false));
        }

        if (user.GoogleAccessToken is null)
            return Result.Failure<SyncPreparation>(ErrorMessages.AutoSyncNotConnected);

        if (user.GoogleCalendarLastSyncedAt is { } lastSync
            && utcNow - lastSync < BackgroundDedupeWindow)
        {
            return Result.Success(new SyncPreparation(user, false));
        }

        user.MarkCalendarSyncAttempt(utcNow);
        await deps.UnitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(new SyncPreparation(user, true));
    }

    private async Task<Result<CalendarAutoSyncResult>> FetchAndProcess(
        User user, string accessToken, RunCalendarAutoSyncCommand request, DateTime utcNow, CancellationToken ct)
    {
        List<CalendarEventItem> fetched;
        try
        {
            fetched = await deps.EventFetcher.FetchAsync(
                accessToken, user.GetSelectedCalendarIds(), updatedMin: null, ct);
        }
        catch (CalendarProviderException ex) when (ex.Kind is CalendarFetchErrorKind.ReconnectRequired or CalendarFetchErrorKind.MissingScope)
        {
            LogReconnectRequired(logger, user.Id, ex.RawErrorCode);
            await HandleReconnectRequired(user, ex.RawErrorCode ?? "reconnect_required", ct);
            return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.ReconnectRequired));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGoogleApiError(logger, ex, user.Id);
            user.MarkCalendarSyncTransientError("google_api_error");
            await deps.UnitOfWork.SaveChangesAsync(ct);
            return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.TransientError));
        }

        await deps.UnitOfWork.SaveChangesAsync(ct);
        try
        {
            return await ConcurrencyRetry.RunAsync(deps.UnitOfWork, async retryToken =>
        {
            var outcome = Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.Idle, AlreadySyncing: true));
            await deps.UnitOfWork.ExecuteInTransactionAsync(async transactionToken =>
            {
                if (!await deps.SyncLock.TryAcquireAsync(user.Id, transactionToken)) return;
                await deps.UnitOfWork.AcquireUserRowLockAsync(user.Id, transactionToken);
                var current = await deps.UserRepository.FindOneTrackedAsync(u => u.Id == user.Id, cancellationToken: transactionToken);
                if (current is null)
                {
                    outcome = Result.Failure<CalendarAutoSyncResult>(ErrorMessages.UserNotFound);
                    return;
                }
                await deps.UserRepository.ReloadAsync(current, transactionToken);
                var gate = await payGate.CanManageCalendar(current.Id, transactionToken);
                if (gate.IsFailure) { outcome = gate.PropagateError<CalendarAutoSyncResult>(); return; }
                if (current.GoogleAccessToken is null || (!current.GoogleCalendarAutoSyncEnabled && !request.IsOpportunistic)
                    || current.GoogleCalendarLastSyncedAt is { } last && utcNow - last < BackgroundDedupeWindow)
                {
                    outcome = Result.Success(new CalendarAutoSyncResult(0, 0, current.GoogleCalendarAutoSyncStatus ?? GoogleCalendarAutoSyncStatus.Idle));
                    return;
                }
                outcome = await ProcessFetched(current, fetched, utcNow, transactionToken);
            }, retryToken);
            return outcome;
        }, ct, exception => exception is DbUpdateConcurrencyException
            || exception is DbUpdateException update && DbUniqueViolation.IsUniqueViolation(update));
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            deps.UnitOfWork.ResetTracking();
            return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.TransientError));
        }
    }

    private async Task<Result<CalendarAutoSyncResult>> ProcessFetched(User user, List<CalendarEventItem> fetched, DateTime utcNow, CancellationToken ct)
    {
        var timeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var normalizedFetched = NormalizeFetchedEvents(user.Id, fetched);
        var habits = await deps.HabitRepository.FindTrackedAsync(h => h.UserId == user.Id, ct);
        var reconciled = ReconcileExistingHabits(user, habits, normalizedFetched, timeZone, utcNow);
        var newSuggestions = await CreateSuggestions(user, habits, normalizedFetched, fetched, timeZone, utcNow, ct);

        if (newSuggestions > 0 && IsInQuietHours(timeZone, utcNow)
            && !await HasRecentSuggestionNotification(user.Id, utcNow, ct))
        {
            await CreateSuggestionNotification(user, newSuggestions, ct);
        }

        await deps.UnitOfWork.SaveChangesAsync(ct);

        user.MarkCalendarSyncSuccess(utcNow);
        await deps.UnitOfWork.SaveChangesAsync(ct);

        return Result.Success(new CalendarAutoSyncResult(newSuggestions, reconciled, GoogleCalendarAutoSyncStatus.Idle));
    }

    private int ReconcileExistingHabits(
        User user, IReadOnlyList<Habit> habits, List<CalendarEventItem> fetched, TimeZoneInfo timeZone, DateTime utcNow)
    {
        var assignedEventIds = habits.Where(h => h.GoogleEventId != null)
            .Select(h => h.GoogleEventId!)
            .ToHashSet(StringComparer.Ordinal);
        var reservedEventIds = new HashSet<string>(assignedEventIds, StringComparer.Ordinal);

        var eventsByKey = fetched
            .Where(ev => !assignedEventIds.Contains(ev.Id))
            // Event links persist projection-derived state, so withheld recurrences must stay unlinked. https://github.com/thomasluizon/orbit-tickets/issues/626
            .Where(ev => !ev.HasUnrepresentableRecurrenceAfterProjection(timeZone))
            .Select(ev => ev.ProjectTo(timeZone))
            .GroupBy(ev => LegacyCalendarMatchKey.BuildLegacyMatchKey(ev.Title, ev.StartDate, ev.StartTime), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Id, StringComparer.Ordinal);

        if (eventsByKey.Count == 0)
        {
            user.MarkCalendarSyncReconciled(utcNow);
            return 0;
        }

        var existingHabits = habits.Where(h => h.GoogleEventId == null);

        var habitsByKey = existingHabits
            .GroupBy(habit => LegacyCalendarMatchKey.BuildLegacyMatchKey(
                habit.Title,
                habit.NextDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                habit.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture)), StringComparer.Ordinal)
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
        User user, IReadOnlyList<Habit> habits, List<CalendarEventItem> fetched, List<CalendarEventItem> fetchedIncludingDuplicates,
        TimeZoneInfo timeZone, DateTime utcNow, CancellationToken ct)
    {
        if (fetched.Count == 0) return 0;

        var habitEventIds = habits.Where(h => h.GoogleEventId != null)
            .Select(h => h.GoogleEventId!)
            .ToHashSet(StringComparer.Ordinal);

        // Reserve ids from ALL suggestions (incl. imported/dismissed): UserId+GoogleEventId is a full unique index, so re-inserting a known event throws 23505. https://thomasluizon.sentry.io/issues/ORBIT-API-E
        var existingSuggestionEventIds = (await deps.SuggestionRepository.ProjectAsync(s => s.UserId == user.Id,
            q => q.Select(s => s.GoogleEventId), ct)).ToHashSet(StringComparer.Ordinal);
        var existingSuggestions = await deps.SuggestionRepository.FindAsync(s => s.UserId == user.Id
            && s.DismissedAtUtc == null && s.ImportedAtUtc == null, ct);
        var reservedEventIds = new HashSet<string>(habitEventIds, StringComparer.Ordinal);
        reservedEventIds.UnionWith(existingSuggestionEventIds);

        var fetchedById = fetchedIncludingDuplicates
            .GroupBy(ev => ev.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var legacyPendingIds = existingSuggestions
            .Where(s => s.DismissedAtUtc is null && s.ImportedAtUtc is null
                && StoredCalendarEventJson.NeedsRecurrenceEvidenceRefresh(s.RawEventJson)
                && fetchedById.TryGetValue(s.GoogleEventId, out var ev)
                && !ev.NeedsRecurrenceEvidenceRefresh)
            .Select(s => s.GoogleEventId)
            .ToHashSet(StringComparer.Ordinal);
        if (legacyPendingIds.Count > 0)
        {
            var pendingTracked = await deps.SuggestionRepository.FindTrackedAsync(
                s => s.UserId == user.Id && s.DismissedAtUtc == null && s.ImportedAtUtc == null, ct);
            foreach (var suggestion in pendingTracked.Where(s => legacyPendingIds.Contains(s.GoogleEventId)))
            {
                var refreshed = fetchedById[suggestion.GoogleEventId];
                suggestion.RefreshPendingEvent(
                    refreshed.Title, ResolveStartDateUtc(refreshed), StoredCalendarEventJson.Serialize(refreshed));
            }
        }

        int created = 0;
        foreach (var ev in fetched)
        {
            if (created >= MaxSuggestionsPerTick) break;
            if (ev.HasUnrepresentableRecurrenceAfterProjection(timeZone)) continue;
            if (!reservedEventIds.Add(ev.Id)) continue;

            var startDateUtc = ResolveStartDateUtc(ev);
            var rawJson = StoredCalendarEventJson.Serialize(ev);

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

        var isPortuguese = LocaleHelper.IsPortuguese(user.Language);
        var notification = Notification.Create(
            user.Id,
            isPortuguese ? "Google Agenda desconectado" : "Google Calendar disconnected",
            isPortuguese
                ? "A sincronização automática está pausada. Reconecte para retomar."
                : "Auto-sync paused. Reconnect to resume.",
            url: NotificationUrls.CalendarSync);
        await deps.NotificationRepository.AddAsync(notification, ct);

        await deps.UnitOfWork.SaveChangesAsync(ct);
    }

    private async Task<bool> HasRecentSuggestionNotification(Guid userId, DateTime utcNow, CancellationToken ct)
    {
        var cutoff = utcNow - NotificationRateLimitWindow;
        return await deps.NotificationRepository.AnyAsync(
            n => n.UserId == userId
                && n.Url == NotificationUrls.CalendarSyncReview
                && n.CreatedAtUtc > cutoff,
            ct);
    }

    private async Task CreateSuggestionNotification(User user, int count, CancellationToken ct)
    {
        var isPortuguese = LocaleHelper.IsPortuguese(user.Language);
        string title;
        if (isPortuguese)
            title = count == 1 ? "1 novo evento do calendário" : $"{count} novos eventos do calendário";
        else
            title = count == 1 ? "1 new calendar event" : $"{count} new calendar events";
        var body = isPortuguese
            ? "Revise antes de importar"
            : "Review before importing";
        var notification = Notification.Create(
            user.Id,
            title,
            body,
            url: NotificationUrls.CalendarSyncReview);
        await deps.NotificationRepository.AddAsync(notification, ct);
    }

    private static bool IsInQuietHours(TimeZoneInfo timeZone, DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone);
        return local.Hour >= QuietHoursStart && local.Hour < QuietHoursEnd;
    }

    private static DateTime ResolveStartDateUtc(CalendarEventItem ev)
    {
        if (ev.StartUtc is { } startUtc)
            return DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);

        return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    }

    private List<CalendarEventItem> NormalizeFetchedEvents(Guid userId, List<CalendarEventItem> fetched)
    {
        if (fetched.Count <= 1)
            return fetched;

        var unique = new List<CalendarEventItem>(fetched.Count);
        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ev in fetched)
            if (seenEventIds.Add(ev.Id)) unique.Add(ev);
        if (fetched.Count != unique.Count)
            LogDuplicateFetchedEventIds(logger, userId, fetched.Count - unique.Count);
        return unique;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Google API error during auto-sync for user {UserId}")]
    private static partial void LogGoogleApiError(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Google Calendar reconnect required during auto-sync for user {UserId} (code: {ErrorCode})")]
    private static partial void LogReconnectRequired(ILogger logger, Guid userId, string? errorCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Skipped {Count} duplicate Google Calendar event ids during auto-sync for user {UserId}")]
    private static partial void LogDuplicateFetchedEventIds(ILogger logger, Guid userId, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Duplicate Google Calendar event id reconciliation skipped for user {UserId}, habit {HabitId}: {GoogleEventId}")]
    private static partial void LogDuplicateReconciliationEventId(ILogger logger, Guid userId, Guid habitId, string googleEventId);
}
