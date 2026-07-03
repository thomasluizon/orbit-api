using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

/// <summary>
/// The daily-summary payload returned to clients. <paramref name="Insight"/> is retained on the
/// contract only for legacy mobile clients (pre-July-2026 builds) that still parse and render a
/// nudge chip; it is always mapped empty regardless of cache contents and is no longer generated.
/// </summary>
public record DailySummaryResponse(string Summary, string Insight, bool FromCache);

public record GetDailySummaryQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string Language) : IRequest<Result<DailySummaryResponse>>;

public class GetDailySummaryQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IGenericRepository<HabitLog> habitLogRepository,
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

        if (!user.AiSummaryEnabled)
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

        if (cache.TryGetValue(cacheKey, out DailySummaryContent? cached) && cached is not null)
        {
            return Result.Success(new DailySummaryResponse(cached.Summary, string.Empty, FromCache: true));
        }

        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && !h.IsGeneral,
            q => q
                .Include(h => h.Logs.Where(l => l.Date >= request.DateFrom && l.Date <= request.DateTo))
                .Include(h => h.Goals),
            cancellationToken);

        var summaryHabits = habits
            .Where(h => !HasSkipLogInRange(h, request.DateFrom, request.DateTo))
            .ToList();

        var lastBadHabitSlipDates = await LoadLastBadHabitSlipDates(
            summaryHabits, userToday, cancellationToken);

        var summaryResult = await summaryService.GenerateSummaryAsync(
            summaryHabits,
            request.DateFrom,
            request.DateTo,
            userToday,
            effectiveLanguage,
            currentLocalTime,
            user.CurrentStreak,
            user.StreakFreezesAccumulated,
            lastBadHabitSlipDates,
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

        cache.Set(cacheKey, summaryResult.Value, cacheOptions);

        return Result.Success(new DailySummaryResponse(
            summaryResult.Value.Summary, string.Empty, FromCache: false));
    }

    private static bool HasSkipLogInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        habit.Logs.Any(l => l.Date >= dateFrom && l.Date <= dateTo && l.Value == 0);

    /// <summary>
    /// Returns the most-recent slip date (a completion log, <c>Value &gt; 0</c>) on or before
    /// <paramref name="userToday"/> for each bad habit, keyed by habit id. The habits' own
    /// <see cref="Habit.Logs"/> are date-windowed to the summary range and therefore cannot answer
    /// "days since last slip" for a slip that fell outside that window, so this runs a separate
    /// query scoped to bad-habit slip rows. Habits with no slip on record are absent from the map.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, DateOnly>> LoadLastBadHabitSlipDates(
        IReadOnlyList<Habit> habits, DateOnly userToday, CancellationToken cancellationToken)
    {
        var badHabitIds = habits.Where(h => h.IsBadHabit).Select(h => h.Id).ToHashSet();
        if (badHabitIds.Count == 0)
            return new Dictionary<Guid, DateOnly>();

        var slipLogs = await habitLogRepository.FindAsync(
            l => badHabitIds.Contains(l.HabitId) && l.Value > 0 && l.Date <= userToday,
            cancellationToken);

        return slipLogs
            .GroupBy(l => l.HabitId)
            .ToDictionary(g => g.Key, g => g.Max(l => l.Date));
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
        $"summary:{userId}:{date:yyyy-MM-dd}:{language}:{timeBucket}";
}
