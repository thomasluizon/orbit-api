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
    DateTime? EndUtc = null,
    string? RecurrenceTimeZone = null)
{
    /// <summary>
    /// The IANA zone the source calendar expands this event's recurrence in, taken from the recurring
    /// master rather than from an expanded instance. Server-only: the <see cref="JsonIgnoreAttribute"/>
    /// keeps this field out of the <c>GET /calendar/events</c> response body.
    /// <c>StoredCalendarEventJson</c> carries it beside the stored suggestion instead, which is what
    /// lets both feeds judge one series the same way. Null for a single event and for a suggestion row
    /// written before that key existed.
    /// </summary>
    [JsonIgnore]
    public string? SourceTimeZone { get; init; }

    /// <summary>The start prescribed by the recurrence, which can differ from an instance moved by hand.</summary>
    [JsonIgnore]
    public DateTime? RecurrenceStartUtc { get; init; }

    internal bool NeedsRecurrenceEvidenceRefresh => IsRecurring && StartTime is not null
        && (string.IsNullOrWhiteSpace(SourceTimeZone) || RecurrenceStartUtc is null);

    /// <summary>
    /// A full turn of both zones' adjustment rules, which is every offset pair a recurrence can meet.
    /// </summary>
    private const int ProbeDays = 366;

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
    /// True when a timed recurrence cannot be shown to keep one account-local weekday pattern and
    /// one displayed start clock across a year in <paramref name="accountTimeZone"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The proof uses the original recurrence start, not the actual start of a rescheduled instance.
    /// It walks source wall clocks through a year because Google's expanded feed covers sixty days.
    /// </para>
    /// <para>
    /// Two zones with the same rules keep the same date and clock after the actual instance is
    /// checked against the recurrence-defined start.
    /// </para>
    /// <para>
    /// Missing source zone or original recurrence start leaves the series unproved and withheld.
    /// </para>
    /// <para>
    /// A <c>BYDAY</c> rule must keep its source weekday. Other rules may keep a fixed date shift, but
    /// all timed recurrences must keep the same displayed start minute.
    /// </para>
    /// </remarks>
    internal bool HasUnrepresentableRecurrenceAfterProjection(TimeZoneInfo accountTimeZone)
    {
        if (!IsRecurring)
            return false;

        if (StartTime is null)
            return false;

        if (StartUtc is null || RecurrenceStartUtc is null || RecurrenceRule is null
            || !TryFindSourceTimeZone(SourceTimeZone, out var sourceTimeZone))
            return true;

        var recurrenceStart = DateTime.SpecifyKind(RecurrenceStartUtc.Value, DateTimeKind.Utc);
        var actualStart = DateTime.SpecifyKind(StartUtc.Value, DateTimeKind.Utc);
        var projectedRecurrenceStart = TimeZoneInfo.ConvertTimeFromUtc(recurrenceStart, accountTimeZone);
        var projectedActualStart = TimeZoneInfo.ConvertTimeFromUtc(actualStart, accountTimeZone);
        if (projectedActualStart.DayOfWeek != projectedRecurrenceStart.DayOfWeek
            || ProjectedClock(projectedActualStart) != ProjectedClock(projectedRecurrenceStart))
            return true;

        if (sourceTimeZone.HasSameRules(accountTimeZone))
            return false;

        return !KeepsItsLocalScheduleForAYear(
            sourceTimeZone, accountTimeZone, recurrenceStart, NamesAWeekday(RecurrenceRule));
    }

    /// <summary>
    /// True when the projection dropped an end time the source carried, so the caller can log the
    /// reason. Deliberately independent of <see cref="EndUtc"/>: a suggestion row written before this
    /// projection existed carries an <see cref="EndTime"/> with no end instant, and those rows are the
    /// ones that lose an end most often.
    /// </summary>
    internal bool DropsEndTimeAfterProjection(CalendarEventItem projected)
        => EndTime is not null && projected.EndTime is null;

    private static bool NamesAWeekday(string recurrenceRule)
    {
        var ruleBody = recurrenceRule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)
            ? recurrenceRule["RRULE:".Length..]
            : recurrenceRule;
        return ruleBody.Split(';').Any(term => term.StartsWith("BYDAY=", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryFindSourceTimeZone(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return false;

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when every source wall clock across a year projects to the same account-local start
    /// minute and day relationship as the recurrence-defined first start.
    /// </summary>
    /// <remarks>
    /// A wall clock a spring-forward gap removes names no instant on that date, so the series produces
    /// no occurrence there and the date carries no evidence either way. The walk moves to the next
    /// date rather than ending. Counting the absent date as a failure withheld a whole series on a
    /// date it never fires, which is the opposite of what the gate exists to prevent.
    /// </remarks>
    private static bool KeepsItsLocalScheduleForAYear(
        TimeZoneInfo sourceTimeZone, TimeZoneInfo accountTimeZone, DateTime recurrenceStartUtc,
        bool namesAWeekday)
    {
        var sourceStart = TimeZoneInfo.ConvertTimeFromUtc(
            recurrenceStartUtc, sourceTimeZone);
        var projectedStart = TimeZoneInfo.ConvertTimeFromUtc(recurrenceStartUtc, accountTimeZone);
        var projectedClock = ProjectedClock(projectedStart);
        var projectedDayOffset = DateOnly.FromDateTime(projectedStart).DayNumber
            - DateOnly.FromDateTime(sourceStart).DayNumber;
        if (namesAWeekday && projectedDayOffset != 0)
            return false;

        var wallClock = TimeOnly.FromDateTime(sourceStart);
        var firstDay = DateOnly.FromDateTime(sourceStart).DayNumber;
        var lastDay = Math.Min(firstDay + ProbeDays, DateOnly.MaxValue.DayNumber);

        for (var dayNumber = firstDay; dayNumber <= lastDay; dayNumber++)
        {
            var probe = DateOnly.FromDayNumber(dayNumber);
            var sourceLocal = probe.ToDateTime(wallClock, DateTimeKind.Unspecified);
            if (sourceTimeZone.IsInvalidTime(sourceLocal))
                continue;

            foreach (var offset in SourceOffsetsAt(sourceTimeZone, sourceLocal))
            {
                var accountLocal = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(sourceLocal - offset, DateTimeKind.Utc), accountTimeZone);
                if (ProjectedClock(accountLocal) != projectedClock
                    || DateOnly.FromDateTime(accountLocal).DayNumber - probe.DayNumber != projectedDayOffset)
                    return false;
            }
        }

        return true;
    }

    private static string ProjectedClock(DateTime local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The offsets the source zone can hold at one wall clock. A fall-back transition repeats an hour,
    /// so an ambiguous wall clock has two instants and both must keep the date.
    /// </summary>
    /// <remarks>
    /// Picking one of the two would need a fact the response does not carry. RFC 5545 section 3.3.5
    /// reads a repeated wall clock as "the first occurrence of the referenced time", while
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> reads it as standard time,
    /// which is the second. The two readings disagree, and which one the source calendar expanded the
    /// occurrence to is not in what Google returns, so requiring both to keep the date withholds a
    /// series the first reading would allow rather than asserting a normalization nothing established.
    /// </remarks>
    private static IReadOnlyList<TimeSpan> SourceOffsetsAt(TimeZoneInfo sourceTimeZone, DateTime sourceLocal)
        => sourceTimeZone.IsAmbiguousTime(sourceLocal)
            ? sourceTimeZone.GetAmbiguousTimeOffsets(sourceLocal)
            : [sourceTimeZone.GetUtcOffset(sourceLocal)];
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

                if (source.HasUnrepresentableRecurrenceAfterProjection(timeZone))
                    continue;

                var projected = source.ProjectTo(timeZone);

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

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return accessToken;
    }

    private async Task<HashSet<string>> BuildImportedEventIdSet(Guid userId, CancellationToken ct)
    {
        var habitEventIds = (await repos.Habits.FindAsync(
                h => h.UserId == userId && h.GoogleEventId != null, ct))
            .Select(h => h.GoogleEventId!)
            .ToList();

        var pendingSuggestionEventIds = (await repos.Suggestions.FindAsync(
                s => s.UserId == userId && s.DismissedAtUtc == null && s.ImportedAtUtc == null, ct))
            .Where(s => !StoredCalendarEventJson.NeedsRecurrenceEvidenceRefresh(s.RawEventJson))
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

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Omitted the end time of calendar event {EventId} for user {UserId}: the projected end does not follow the projected start on the projected start date")]
    private static partial void LogProjectedEndTimeOmitted(ILogger logger, string eventId, Guid userId);
}
