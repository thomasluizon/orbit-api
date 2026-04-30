using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record DailySummaryResponse(string Summary, bool FromCache);

public record GetDailySummaryQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    bool IncludeOverdue,
    string Language) : IRequest<Result<DailySummaryResponse>>;

public class GetDailySummaryQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IPayGateService payGate,
    ISummaryService summaryService,
    IMemoryCache cache) : IRequestHandler<GetDailySummaryQuery, Result<DailySummaryResponse>>
{
    public async Task<Result<DailySummaryResponse>> Handle(
        GetDailySummaryQuery request,
        CancellationToken cancellationToken)
    {
        // Check pay gate
        var gateCheck = await payGate.CanUseDailySummary(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<DailySummaryResponse>();

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<DailySummaryResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        if (!user.AiSummaryEnabled)
            return Result.Failure<DailySummaryResponse>("AI summary is disabled.");

        // The backend is authoritative about the user's language: prefer the persisted
        // profile language, fall back to the request-supplied value, finally English.
        string effectiveLanguage;
        if (!string.IsNullOrWhiteSpace(user.Language))
            effectiveLanguage = user.Language!;
        else if (!string.IsNullOrWhiteSpace(request.Language))
            effectiveLanguage = request.Language;
        else
            effectiveLanguage = "en";

        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, userTimeZone);
        var userToday = DateOnly.FromDateTime(userNow);
        var currentLocalTime = request.DateFrom == userToday && request.DateTo == request.DateFrom
            ? TimeOnly.FromDateTime(userNow)
            : (TimeOnly?)null;

        var cacheKey = CacheKey(
            request.UserId,
            request.DateFrom,
            effectiveLanguage,
            SummaryTimeBucket(currentLocalTime));

        if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            return Result.Success(new DailySummaryResponse(cached, FromCache: true));
        }

        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && !h.IsGeneral,
            q => q.Include(h => h.Logs.Where(l => l.Date >= request.DateFrom && l.Date <= request.DateTo)),
            cancellationToken);

        var summaryHabits = habits
            .Where(h => !HasSkipLogInRange(h, request.DateFrom, request.DateTo))
            .ToList();

        var summaryResult = await summaryService.GenerateSummaryAsync(
            summaryHabits,
            request.DateFrom,
            request.DateTo,
            request.IncludeOverdue,
            effectiveLanguage,
            currentLocalTime,
            cancellationToken);

        if (summaryResult.IsFailure)
            return Result.Failure<DailySummaryResponse>(summaryResult.Error);

        // Cache until end of day, minimum 5 minutes if expiry is in the past
        var endOfDay = new DateTimeOffset(request.DateFrom.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        var expiry = endOfDay > DateTimeOffset.UtcNow ? endOfDay : DateTimeOffset.UtcNow.AddMinutes(5);

        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = expiry
        };

        cache.Set(cacheKey, summaryResult.Value, cacheOptions);

        return Result.Success(new DailySummaryResponse(summaryResult.Value, FromCache: false));
    }

    private static bool HasSkipLogInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        habit.Logs.Any(l => l.Date >= dateFrom && l.Date <= dateTo && l.Value == 0);

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
