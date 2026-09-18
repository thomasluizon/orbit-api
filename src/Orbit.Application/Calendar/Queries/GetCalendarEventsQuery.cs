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
    /// The IANA zone the source calendar expands this event's recurrence in, taken from the recurring
    /// master rather than from an expanded instance. Server-only: the <see cref="JsonIgnoreAttribute"/>
    /// keeps it out of the <c>GET /calendar/events</c> response body, so no client contract changes.
    /// <c>StoredCalendarEventJson</c> carries it beside the stored suggestion instead, which is what
    /// lets both feeds judge one series the same way. Null for a single event and for a suggestion row
    /// written before that key existed.
    /// </summary>
    [JsonIgnore]
    public string? SourceTimeZone { get; init; }

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
    /// True when this event carries a <c>BYDAY</c> rule that the projection into
    /// <paramref name="accountTimeZone"/> cannot be proved to leave alone for a whole year, so the
    /// weekday the rule names and the day the account sees can disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The proof runs over both zones' own offset rules, never over the occurrences Google returned.
    /// <c>GoogleCalendarApi</c> asks for sixty days, so an expanded-instance sample can miss the
    /// transition entirely: a January fetch of a 03:30 <c>Europe/Lisbon</c> <c>BYDAY=TH</c> series is
    /// Thursday 00:30 in <c>America/Sao_Paulo</c> at every instance in the window, and Wednesday 23:30
    /// from the Lisbon transition on 2027-03-29 onward. A window that cannot reach the transition
    /// cannot prove the series, so the decision comes from <see cref="TimeZoneInfo"/> instead.
    /// </para>
    /// <para>
    /// A source zone the process cannot resolve leaves the series unproved and therefore withheld.
    /// <c>TimeZoneHelper.FindTimeZone</c> is deliberately not used for it: that helper answers
    /// <see cref="TimeZoneInfo.Utc"/> for an unknown id, which would assert a stability nothing
    /// established.
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
    internal bool HasUnrepresentableRecurrenceAfterProjection(TimeZoneInfo accountTimeZone)
    {
        if (RecurrenceRule is null || !NamesAWeekday(RecurrenceRule))
            return false;

        if (StartTime is null || StartUtc is null)
            return false;

        if (!TryFindSourceTimeZone(SourceTimeZone, out var sourceTimeZone))
            return true;

        return !KeepsItsLocalDateForAYear(sourceTimeZone, accountTimeZone, StartUtc.Value);
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
    /// True when the occurrence's source wall clock lands on the same account-local date on every
    /// date of the year that follows it. A recurrence keeps one wall clock in its own zone, so walking
    /// every date once at that wall clock covers every occurrence any <c>BYDAY</c> rule can produce,
    /// through both zones' transitions in both hemispheres.
    /// </summary>
    private static bool KeepsItsLocalDateForAYear(
        TimeZoneInfo sourceTimeZone, TimeZoneInfo accountTimeZone, DateTime startUtc)
    {
        var sourceStart = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), sourceTimeZone);
        var wallClock = TimeOnly.FromDateTime(sourceStart);
        var firstDay = DateOnly.FromDateTime(sourceStart).DayNumber;
        var lastDay = Math.Min(firstDay + ProbeDays, DateOnly.MaxValue.DayNumber);

        for (var dayNumber = firstDay; dayNumber <= lastDay; dayNumber++)
        {
            var probe = DateOnly.FromDayNumber(dayNumber);
            var sourceLocal = probe.ToDateTime(wallClock, DateTimeKind.Unspecified);
            if (sourceTimeZone.IsInvalidTime(sourceLocal))
                return false;

            foreach (var offset in SourceOffsetsAt(sourceTimeZone, sourceLocal))
            {
                var accountLocal = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(sourceLocal - offset, DateTimeKind.Utc), accountTimeZone);
                if (DateOnly.FromDateTime(accountLocal) != probe)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The offsets the source zone can hold at one wall clock. A fall-back transition repeats an hour,
    /// so an ambiguous wall clock has two instants and both must keep the date.
    /// </summary>
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
