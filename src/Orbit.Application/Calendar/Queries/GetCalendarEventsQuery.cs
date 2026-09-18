using System.Globalization;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Behaviors;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Queries;

public record CalendarEventItem(
    string Id,
    string Title,
    string? Description,
    string? StartDate,
    string? StartTime,
    string? EndTime,
    bool IsRecurring,
    string? RecurrenceRule,
    List<int> Reminders,
    DateTime? StartUtc = null,
    string CalendarId = "",
    string CalendarName = "",
    DateTime? EndUtc = null)
{
    /// <summary>
    /// Every expanded instance Google returned for this recurring master inside the fetch window,
    /// each carrying the source zone's own offset at that instant. Server-only: the
    /// <see cref="JsonIgnoreAttribute"/> keeps it out of the <c>GET /calendar/events</c> response
    /// body and out of the stored suggestion JSON, so no client contract changes. Empty for a single
    /// event, for an all-day series, and for a suggestion row read back from the database.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<DateTimeOffset> ExpandedOccurrences { get; init; } = [];

    internal CalendarEventItem ProjectTo(TimeZoneInfo timeZone)
    {
        if (StartTime is null || StartUtc is null)
            return this;

        var localStart = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(StartUtc.Value, DateTimeKind.Utc),
            timeZone);
        var localEnd = EndUtc is { } endUtc
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc), timeZone)
            : (DateTime?)null;
        var projectedStartDate = localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var projectedStartTime = localStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        var projectedEndTime = localEnd?.ToString("HH:mm", CultureInfo.InvariantCulture);

        return this with
        {
            StartDate = projectedStartDate,
            StartTime = projectedStartTime,
            EndTime = localEnd is { } sameDayEnd
                && sameDayEnd.Date == localStart.Date
                && string.CompareOrdinal(projectedEndTime, projectedStartTime) > 0
                ? projectedEndTime
                : null
        };
    }

    /// <summary>
    /// True when a <c>BYDAY</c> rule names one weekday in the source calendar but names another
    /// weekday for at least one occurrence once projected into <paramref name="timeZone"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate walks <see cref="ExpandedOccurrences"/>, not the single sampled instance, because a
    /// series can be stable at its first occurrence and shift at a later one: an
    /// <c>America/Sao_Paulo</c> account reading a 03:30 <c>Europe/Lisbon</c> <c>BYDAY=TH</c> series
    /// sees Thursday in January and Wednesday from the Lisbon transition onward. Sampling January
    /// alone would ship a rule that is wrong for half the year. When the list is empty the check
    /// falls back to the sampled instance: an all-day series carries no instant, and a suggestion
    /// read back from the database drops the list because it is <see cref="JsonIgnoreAttribute"/>d.
    /// </para>
    /// <para>
    /// Refusal here and the clamp in <c>HabitScheduleService.IsMonthlyMatch</c> answer two different
    /// questions on purpose. The scheduler clamps a rule Orbit OWNS, already expressed in the user's
    /// own timezone, so "monthly on the 31st" firing on 28 February keeps the user's stated intent.
    /// This gate judges a rule Orbit IMPORTS and does not own. A Lisbon <c>BYDAY=TH</c> rule cannot be
    /// re-expressed in Sao Paulo without picking a weekday the source never named, and either choice
    /// is wrong for half the year, so Orbit refuses rather than invents. The two compose: refuse at
    /// the import boundary, then apply best effort inside, because an event that passes this gate
    /// becomes an Orbit-owned habit whose rule the scheduler may then clamp.
    /// </para>
    /// </remarks>
    internal bool HasUnrepresentableRecurrenceAfterProjection(CalendarEventItem projected, TimeZoneInfo timeZone)
    {
        if (RecurrenceRule is null || !NamesAWeekday(RecurrenceRule))
            return false;

        if (ExpandedOccurrences.Count == 0)
            return !string.Equals(StartDate, projected.StartDate, StringComparison.Ordinal);

        return ExpandedOccurrences.Any(occurrence =>
            TimeZoneInfo.ConvertTimeFromUtc(occurrence.UtcDateTime, timeZone).Date != occurrence.Date);
    }

    /// <summary>
    /// True when the projection dropped a real end time, so the caller can log the reason. The source
    /// carried both instants, yet the projected end no longer follows the projected start on the
    /// projected start date. <see cref="EndUtc"/> still carries the real duration for the client.
    /// </summary>
    internal bool DropsEndTimeAfterProjection(CalendarEventItem projected)
        => StartTime is not null
            && StartUtc is not null
            && EndUtc is not null
            && projected.EndTime is null;

    private static bool NamesAWeekday(string recurrenceRule)
    {
        var ruleBody = recurrenceRule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)
            ? recurrenceRule["RRULE:".Length..]
            : recurrenceRule;
        return ruleBody.Split(';').Any(term => term.StartsWith("BYDAY=", StringComparison.OrdinalIgnoreCase));
    }
}

public record GetCalendarEventsQuery(Guid UserId) : IRequest<Result<List<CalendarEventItem>>>, IConcurrencyRetryable;

/// <summary>Groups the repositories the calendar events query touches to keep the handler constructor small.</summary>
public record GetCalendarEventsRepositories(
    IGenericRepository<User> Users,
    IGenericRepository<Habit> Habits,
    IGenericRepository<GoogleCalendarSyncSuggestion> Suggestions);

public partial class GetCalendarEventsQueryHandler(
    GetCalendarEventsRepositories repos,
    IPayGateService payGate,
    IGoogleTokenService googleTokenService,
    ICalendarEventFetcher eventFetcher,
    IUnitOfWork unitOfWork,
    ILogger<GetCalendarEventsQueryHandler> logger) : IRequestHandler<GetCalendarEventsQuery, Result<List<CalendarEventItem>>>
{
    public async Task<Result<List<CalendarEventItem>>> Handle(GetCalendarEventsQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessCalendar(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<List<CalendarEventItem>>();

        var user = await repos.Users.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<List<CalendarEventItem>>(ErrorMessages.UserNotFound);

        var accessToken = await ResolveAccessTokenAsync(user, cancellationToken);
        if (accessToken is null)
            return Result.Failure<List<CalendarEventItem>>(ErrorMessages.CalendarNotConnected);

        try
        {
            var fetched = await eventFetcher.FetchAsync(
                accessToken, user.GetSelectedCalendarIds(), updatedMin: null, cancellationToken);

            var importedEventIds = await BuildImportedEventIdSet(request.UserId, cancellationToken);
            var timeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, request.UserId);
            var items = new List<CalendarEventItem>();
            foreach (var source in fetched)
            {
                if (importedEventIds.Contains(source.Id))
                    continue;

                var projected = source.ProjectTo(timeZone);
                if (source.HasUnrepresentableRecurrenceAfterProjection(projected, timeZone))
                    continue;

                if (source.DropsEndTimeAfterProjection(projected))
                    LogProjectedEndTimeOmitted(logger, source.Id, request.UserId);

                items.Add(projected);
            }

            return Result.Success(items);
        }
        catch (CalendarProviderException ex)
        {
            if (ex.Kind == CalendarFetchErrorKind.ReconnectRequired)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    LogGoogleCalendarReconnectRequired(logger, request.UserId, ex.RawErrorCode);
                user.MarkCalendarSyncReconnectRequired(ex.RawErrorCode ?? "reconnect_required");
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Failure<List<CalendarEventItem>>(ErrorMessages.CalendarReconnectRequired);
            }
            LogGoogleCalendarApiError(logger, ex, request.UserId);
            return Result.Failure<List<CalendarEventItem>>(ErrorMessages.CalendarFetchFailed);
        }
    }

    private async Task<string?> ResolveAccessTokenAsync(User user, CancellationToken cancellationToken)
    {
        if (user.GoogleRefreshToken is null)
        {
            var existingAccessToken = await googleTokenService.GetValidAccessTokenAsync(user, cancellationToken);
            if (existingAccessToken is not null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return existingAccessToken;
        }

        var refresh = await googleTokenService.TryRefreshAsync(user, cancellationToken);

        if (refresh.Result == GoogleTokenRefreshResult.RefreshTokenInvalid)
        {
            user.MarkCalendarSyncReconnectRequired(refresh.ErrorCode ?? "invalid_grant");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }

        var accessToken = refresh.AccessToken ?? user.GoogleAccessToken;
        if (accessToken is null)
            return null;

        await unitOfWork.SaveChangesAsync(cancellationToken); return accessToken;
    }

    private async Task<HashSet<string>> BuildImportedEventIdSet(Guid userId, CancellationToken ct)
    {
        var habitEventIds = (await repos.Habits.FindAsync(
                h => h.UserId == userId && h.GoogleEventId != null, ct))
            .Select(h => h.GoogleEventId!)
            .ToList();

        var pendingSuggestionEventIds = (await repos.Suggestions.FindAsync(
                s => s.UserId == userId && s.DismissedAtUtc == null && s.ImportedAtUtc == null, ct))
            .Select(s => s.GoogleEventId)
            .ToList();

        var set = new HashSet<string>(habitEventIds, StringComparer.Ordinal);
        foreach (var id in pendingSuggestionEventIds)
            set.Add(id);
        return set;
    }

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Google Calendar API error for user {UserId}")]
    private static partial void LogGoogleCalendarApiError(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Google Calendar reconnect required for user {UserId} (code: {ErrorCode})")]
    private static partial void LogGoogleCalendarReconnectRequired(ILogger logger, Guid userId, string? errorCode);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Omitted the end time of calendar event {EventId} for user {UserId}: the projected end does not follow the projected start on the projected start date. EndUtc still carries the duration")]
    private static partial void LogProjectedEndTimeOmitted(ILogger logger, string eventId, Guid userId);
}
