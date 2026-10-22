using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

/// <summary>
/// The daily-summary payload returned to clients. <paramref name="Insight"/> is retained on the
/// contract only for legacy mobile clients (pre-July-2026 builds) that still parse and render a
/// nudge chip; it is always mapped empty regardless of cache contents and is no longer generated.
/// </summary>
public record DailySummaryResponse(string Summary, string Insight, bool FromCache,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Orbit.Application.Chat.DaySummaryCard? ReadCard = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SourceRevision = null);

public record GetDailySummaryQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string Language, bool GenerateNarrative = true) : IRequest<Result<DailySummaryResponse>>;

public record HabitSummarySlipDate(Guid HabitId, DateOnly Date);

public class GetDailySummaryQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IHabitSummaryLogReader summaryLogReader,
    IPayGateService payGate,
    ISummaryService summaryService,
    IMemoryCache cache) : IRequestHandler<GetDailySummaryQuery, Result<DailySummaryResponse>>
{
    public async Task<Result<DailySummaryResponse>> Handle(
        GetDailySummaryQuery request,
        CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanUseDailySummary(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<DailySummaryResponse>();

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<DailySummaryResponse>(ErrorMessages.UserNotFound);

        if (request.GenerateNarrative && !user.AiSummaryEnabled)
            return Result.Failure<DailySummaryResponse>(ErrorMessages.AiSummaryDisabled);

        string effectiveLanguage;
        if (!string.IsNullOrWhiteSpace(user.Language))
            effectiveLanguage = user.Language!;
        else if (!string.IsNullOrWhiteSpace(request.Language))
            effectiveLanguage = request.Language;
        else
            effectiveLanguage = "en";

        var nowAtUtc = DateTime.UtcNow;
        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(nowAtUtc, userTimeZone);
        var userToday = DateOnly.FromDateTime(userNow);
        var currentLocalTime = request.DateFrom == userToday && request.DateTo == request.DateFrom
            ? TimeOnly.FromDateTime(userNow)
            : (TimeOnly?)null;

        var cacheKey = CacheKey(
            request.UserId,
            request.DateFrom,
            effectiveLanguage,
            SummaryTimeBucket(currentLocalTime));

        cache.TryGetValue(cacheKey, out ScopedSummary? cached);
        var fromCache = request.GenerateNarrative && cached is not null
            && cached.From == request.DateFrom && cached.To == request.DateTo;

        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && !h.IsGeneral,
            q => q.Include(h => h.Goals),
            cancellationToken);

        var windows = BuildLogWindows(habits, request.DateFrom, request.DateTo, userToday, user.WeekStartDay);
        var logFacts = await summaryLogReader.ReadAsync(windows, cancellationToken);
        var logsByHabit = logFacts
            .GroupBy(log => log.HabitId)
            .ToDictionary(group => group.Key, group => group
                .Select(log => HabitLog.FromSummaryRead(log.HabitId, log.Date, log.Value)).ToList());
        foreach (var habit in habits)
            habit.LoadScheduleLogsForRead(logsByHabit.GetValueOrDefault(habit.Id) ?? []);

        var summaryHabits = habits.ToList();
        var dueDateResolution = summaryHabits
            .Where(habit => habit.Logs.Any(log => log.Date == habit.DueDate && log.Value >= 0))
            .Select(habit => habit.Id)
            .ToHashSet();

        var lastBadHabitSlipDates = await LoadLastBadHabitSlipDates(
            summaryHabits, userToday, cancellationToken);

        var (readCard, revision) = DailySummaryReadProjection.Project(summaryHabits,
            request.DateFrom, request.DateTo, userTimeZone, user.WeekStartDay, user.CurrentStreak);
        if (!request.GenerateNarrative || fromCache)
            return Result.Success(new DailySummaryResponse(fromCache ? cached!.Content.Summary : string.Empty,
                string.Empty, fromCache, readCard, revision));

        var summaryResult = await summaryService.GenerateSummaryAsync(
            summaryHabits,
            new DailySummaryContext(
                request.DateFrom,
                request.DateTo,
                userToday,
                effectiveLanguage,
                currentLocalTime,
                user.CurrentStreak,
                user.StreakFreezesAccumulated,
                lastBadHabitSlipDates,
                user.WeekStartDay,
                dueDateResolution,
                UserId: request.UserId),
            cancellationToken);

        if (summaryResult.IsFailure)
            return summaryResult.PropagateError<DailySummaryResponse>();

        var localEndOfDay = DateTime.SpecifyKind(request.DateFrom.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Unspecified);
        var endOfDay = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEndOfDay, userTimeZone), TimeSpan.Zero);
        var expiry = endOfDay > DateTimeOffset.UtcNow ? endOfDay : DateTimeOffset.UtcNow.AddMinutes(5);

        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = expiry
        };

        cache.Set(cacheKey, new ScopedSummary(request.DateFrom, request.DateTo, summaryResult.Value), cacheOptions);

        return Result.Success(new DailySummaryResponse(
            summaryResult.Value.Summary, string.Empty, FromCache: false, readCard, revision));
    }

    private sealed record ScopedSummary(DateOnly From, DateOnly To, DailySummaryContent Content);

    private static IReadOnlyList<HabitSummaryLogWindow> BuildLogWindows(
        IEnumerable<Habit> habits, DateOnly dateFrom, DateOnly dateTo, DateOnly userToday, int weekStartDay)
    {
        var result = new List<HabitSummaryLogWindow>();
        foreach (var habit in habits)
        {
            var windows = new List<HabitSummaryLogWindow>
            {
                new(habit.Id, dateFrom, dateTo)
            };
            var historyTo = dateTo < userToday ? dateTo : userToday;
            var historyFrom = historyTo.AddDays(-Math.Min(AppConstants.DailySummaryHistoryDays - 1, historyTo.DayNumber));
            windows.Add(new HabitSummaryLogWindow(habit.Id, historyFrom, historyTo));
            if (habit.IsFlexible && habit.FrequencyUnit is not null)
            {
                windows.Add(new HabitSummaryLogWindow(habit.Id,
                    HabitScheduleService.GetWindowStart(habit, dateFrom, weekStartDay),
                    HabitScheduleService.GetWindowEnd(habit, dateTo, weekStartDay)));
            }
            if (!habit.IsBadHabit && !habit.IsFlexible && !habit.IsCompleted
                && habit.FrequencyUnit is not null && habit.DueDate < userToday
                && !HabitScheduleService.IsHabitDueOnDate(habit, userToday, weekStartDay))
            {
                var latest = userToday.AddDays(-1);
                if (habit.EndDate.HasValue && habit.EndDate.Value < latest)
                    latest = habit.EndDate.Value;

                if (latest >= habit.DueDate)
                {
                    var earliest = latest.AddDays(-Math.Min(AppConstants.MaxRangeDays - 1, latest.DayNumber));
                    var from = habit.DueDate > earliest ? habit.DueDate : earliest;
                    windows.AddRange(HabitScheduleService.GetScheduledDates(habit, from, latest, weekStartDay)
                        .Select(date => new HabitSummaryLogWindow(habit.Id, date, date)));
                }

                if ((!habit.EndDate.HasValue || habit.DueDate <= habit.EndDate.Value)
                    && HabitScheduleService.IsHabitDueOnDate(habit, habit.DueDate, weekStartDay))
                    windows.Add(new HabitSummaryLogWindow(habit.Id, habit.DueDate, habit.DueDate));
            }

            foreach (var window in windows.OrderBy(window => window.From))
            {
                if (result.Count > 0 && result[^1].HabitId == habit.Id
                    && window.From.DayNumber <= result[^1].To.DayNumber + 1)
                {
                    var previous = result[^1];
                    result[^1] = previous with { To = window.To > previous.To ? window.To : previous.To };
                }
                else
                {
                    result.Add(window);
                }
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<Guid, DateOnly>> LoadLastBadHabitSlipDates(
        IReadOnlyList<Habit> habits, DateOnly userToday, CancellationToken cancellationToken)
    {
        var badHabitIds = habits.Where(h => h.IsBadHabit).Select(h => h.Id).ToHashSet();
        if (badHabitIds.Count == 0)
            return new Dictionary<Guid, DateOnly>();

        var slipDates = await habitLogRepository.ProjectAsync(
            l => badHabitIds.Contains(l.HabitId) && l.Value > 0 && l.Date <= userToday,
            query => query.GroupBy(log => log.HabitId)
                .Select(group => new HabitSummarySlipDate(group.Key, group.Max(log => log.Date))),
            cancellationToken);

        return slipDates.ToDictionary(row => row.HabitId, row => row.Date);
    }

    private static string SummaryTimeBucket(TimeOnly? currentLocalTime)
    {
        if (!currentLocalTime.HasValue) return "timeless";

        var hour = currentLocalTime.Value.Hour;
        if (hour < 11) return "morning";
        if (hour < 17) return "afternoon";
        if (hour < 21) return "evening";
        return "night";
    }

    private static string CacheKey(Guid userId, DateOnly date, string language, string timeBucket) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"summary:{userId}:{date:yyyy-MM-dd}:{language}:{timeBucket}");
}
