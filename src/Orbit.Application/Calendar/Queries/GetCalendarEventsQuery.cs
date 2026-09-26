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
    [JsonIgnore]
    public string? SourceTimeZone { get; init; }

    /// <summary>The start prescribed by the recurrence, which can differ from an instance moved by hand.</summary>
    [JsonIgnore]
    public DateTime? RecurrenceStartUtc { get; init; }

    /// <summary>The recurrence-defined starts Google expanded inside its bounded event feed.</summary>
    [JsonIgnore]
    public IReadOnlyList<DateTime>? ExpandedOccurrencesUtc { get; init; }

    internal bool NeedsRecurrenceEvidenceRefresh => IsRecurring && StartTime is not null
        && (string.IsNullOrWhiteSpace(SourceTimeZone) || RecurrenceStartUtc is null
            || ExpandedOccurrencesUtc is null);

    /// <summary>
    /// A full turn of both zones' adjustment rules, which is every offset pair a recurrence can meet.
    /// </summary>
    private const int ProbeDays = 366;
    private static readonly string[] Weekdays = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

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
            RecurrenceRule = ProjectedRecurrenceRule(timeZone),
            EndTime = localEnd is { } sameDayEnd
                && sameDayEnd.Date == localStart.Date
                && string.CompareOrdinal(projectedEndTime, projectedStartTime) > 0
                ? projectedEndTime
                : null
        };
    }

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
        var sourceRecurrenceStart = TimeZoneInfo.ConvertTimeFromUtc(recurrenceStart, sourceTimeZone);
        var projectedRecurrenceStart = TimeZoneInfo.ConvertTimeFromUtc(recurrenceStart, accountTimeZone);
        var projectedActualStart = TimeZoneInfo.ConvertTimeFromUtc(actualStart, accountTimeZone);
        if (projectedActualStart.DayOfWeek != projectedRecurrenceStart.DayOfWeek
            || ProjectedClock(projectedActualStart) != ProjectedClock(projectedRecurrenceStart))
            return true;

        if (sourceTimeZone.HasSameRules(accountTimeZone))
            return false;

        var dayShift = DateOnly.FromDateTime(projectedRecurrenceStart).DayNumber
            - DateOnly.FromDateTime(sourceRecurrenceStart).DayNumber;
        if (dayShift != 0 && NamesAWeekday(RecurrenceRule))
            return !TryShiftByDayRule(sourceTimeZone, accountTimeZone, recurrenceStart, dayShift, out _);

        return !KeepsItsLocalScheduleForAYear(sourceTimeZone, accountTimeZone, recurrenceStart);
    }

    private string? ProjectedRecurrenceRule(TimeZoneInfo accountTimeZone)
    {
        if (RecurrenceRule is null || RecurrenceStartUtc is null
            || !TryFindSourceTimeZone(SourceTimeZone, out var sourceTimeZone))
            return RecurrenceRule;

        var recurrenceStart = DateTime.SpecifyKind(RecurrenceStartUtc.Value, DateTimeKind.Utc);
        var sourceStart = TimeZoneInfo.ConvertTimeFromUtc(recurrenceStart, sourceTimeZone);
        var accountStart = TimeZoneInfo.ConvertTimeFromUtc(recurrenceStart, accountTimeZone);
        var dayShift = DateOnly.FromDateTime(accountStart).DayNumber
            - DateOnly.FromDateTime(sourceStart).DayNumber;
        return dayShift != 0 && TryShiftByDayRule(
                sourceTimeZone, accountTimeZone, recurrenceStart, dayShift, out var shiftedRule)
            ? shiftedRule
            : RecurrenceRule;
    }

    private bool TryShiftByDayRule(
        TimeZoneInfo sourceTimeZone, TimeZoneInfo accountTimeZone, DateTime recurrenceStartUtc,
        int dayShift, out string? shiftedRule)
    {
        shiftedRule = null;
        if (RecurrenceRule is null || ExpandedOccurrencesUtc is not { Count: > 0 } occurrences
            || !occurrences.Contains(recurrenceStartUtc))
            return false;
        var parsed = ParseShiftableByDayRule(RecurrenceRule);
        if (parsed is null)
            return false;

        var (terms, frequencyIndex, byDayIndex, tokens, weekStartIndex, normalizeWeekly, shiftWeekStart) = parsed.Value;

        if (!KeepsItsLocalScheduleForAYear(sourceTimeZone, accountTimeZone, recurrenceStartUtc))
            return false;

        var sourceStart = TimeZoneInfo.ConvertTimeFromUtc(recurrenceStartUtc, sourceTimeZone);
        if (!tokens.Contains(Weekdays[(int)sourceStart.DayOfWeek], StringComparer.Ordinal))
            return false;
        var accountStart = TimeZoneInfo.ConvertTimeFromUtc(recurrenceStartUtc, accountTimeZone);
        var accountClock = ProjectedClock(accountStart);
        foreach (var occurrence in occurrences)
        {
            var utc = DateTime.SpecifyKind(occurrence, DateTimeKind.Utc);
            var sourceLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, sourceTimeZone);
            var accountLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, accountTimeZone);
            if (TimeOnly.FromDateTime(sourceLocal) != TimeOnly.FromDateTime(sourceStart)
                || ProjectedClock(accountLocal) != accountClock
                || DateOnly.FromDateTime(accountLocal).DayNumber
                    - DateOnly.FromDateTime(sourceLocal).DayNumber != dayShift)
                return false;
        }

        terms[byDayIndex] = terms[byDayIndex][.."BYDAY=".Length]
            + string.Join(',', tokens.Select(token => ShiftWeekday(token, dayShift)));
        if (normalizeWeekly)
        {
            terms[frequencyIndex] = terms[frequencyIndex].Replace("FREQ=WEEKLY", "FREQ=DAILY", StringComparison.Ordinal);
            terms = terms.Where(term => !term.StartsWith("WKST=", StringComparison.Ordinal)).ToArray();
        }
        else if (shiftWeekStart)
        {
            if (weekStartIndex is { } index)
            {
                var weekStartTerm = terms[index];
                terms[index] = weekStartTerm[.."WKST=".Length]
                    + ShiftWeekday(weekStartTerm["WKST=".Length..], dayShift);
            }
            else
            {
                terms = [.. terms, "WKST=" + ShiftWeekday("MO", dayShift)];
            }
        }
        shiftedRule = string.Join(';', terms);
        return true;
    }

    private static (string[] Terms, int FrequencyIndex, int ByDayIndex, string[] Tokens,
        int? WeekStartIndex, bool NormalizeWeekly, bool ShiftWeekStart)?
        ParseShiftableByDayRule(string recurrenceRule)
    {
        var terms = recurrenceRule.Split(';');
        if (terms.Any(term => !IsSupportedRuleTerm(term)))
            return null;

        var frequencies = terms.Select((term, index) => (term, index))
            .Where(entry => entry.term.StartsWith("RRULE:FREQ=", StringComparison.Ordinal)
                || entry.term.StartsWith("FREQ=", StringComparison.Ordinal)).ToList();
        if (frequencies.Count != 1)
            return null;

        var frequency = frequencies[0].term;
        var frequencyValue = frequency[(frequency.StartsWith("RRULE:", StringComparison.Ordinal)
            ? "RRULE:FREQ=".Length : "FREQ=".Length)..];
        if (frequencyValue is not ("WEEKLY" or "DAILY"))
            return null;

        var byDays = terms.Select((term, index) => (term, index))
            .Where(entry => entry.term.StartsWith("BYDAY=", StringComparison.Ordinal))
            .ToList();
        if (byDays.Count != 1)
            return null;

        var tokens = byDays[0].term["BYDAY=".Length..].Split(',');
        if (tokens.Length == 0 || tokens.Any(token =>
                !Weekdays.Contains(token, StringComparer.Ordinal)))
            return null;

        var intervals = terms.Where(term => term.StartsWith("INTERVAL=", StringComparison.Ordinal))
            .ToList();
        if (intervals.Count > 1)
            return null;

        var interval = 1;
        if (intervals.Count == 1 && (!int.TryParse(intervals[0]["INTERVAL=".Length..],
                NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1))
            return null;
        if ((frequencyValue == "DAILY" && interval > 1)
            || (frequencyValue == "WEEKLY" && interval > 1 && tokens.Length != 1))
            return null;

        var weekStarts = terms.Select((term, index) => (term, index))
            .Where(entry => entry.term.StartsWith("WKST=", StringComparison.Ordinal))
            .ToList();
        if (weekStarts.Count > 1 || (weekStarts.Count == 1
            && !Weekdays.Contains(weekStarts[0].term["WKST=".Length..], StringComparer.Ordinal)))
            return null;

        var weekStartIndex = weekStarts.Count == 1 ? weekStarts[0].index : (int?)null;
        return (terms, frequencies[0].index, byDays[0].index, tokens, weekStartIndex,
            frequencyValue == "WEEKLY" && interval == 1,
            frequencyValue == "WEEKLY" && interval > 1);
    }

    private static bool IsSupportedRuleTerm(string term)
        => term.StartsWith("RRULE:FREQ=", StringComparison.Ordinal)
            || term.StartsWith("FREQ=", StringComparison.Ordinal)
            || term.StartsWith("BYDAY=", StringComparison.Ordinal)
            || term.StartsWith("INTERVAL=", StringComparison.Ordinal)
            || term.StartsWith("WKST=", StringComparison.Ordinal);

    private static string ShiftWeekday(string token, int dayShift)
    {
        var index = Array.FindIndex(Weekdays, day =>
            string.Equals(day, token, StringComparison.OrdinalIgnoreCase));
        return Weekdays[((index + dayShift) % 7 + 7) % 7];
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

    private static bool KeepsItsLocalScheduleForAYear(
        TimeZoneInfo sourceTimeZone, TimeZoneInfo accountTimeZone, DateTime recurrenceStartUtc)
    {
        var sourceStart = TimeZoneInfo.ConvertTimeFromUtc(
            recurrenceStartUtc, sourceTimeZone);
        var projectedStart = TimeZoneInfo.ConvertTimeFromUtc(recurrenceStartUtc, accountTimeZone);
        var projectedClock = ProjectedClock(projectedStart);
        var projectedDayOffset = DateOnly.FromDateTime(projectedStart).DayNumber
            - DateOnly.FromDateTime(sourceStart).DayNumber;
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
