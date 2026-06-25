using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Queries;

public record RetrospectiveHabitStat(
    string Name,
    string? Emoji,
    int CompletionRate,
    int CompletedCount,
    int ScheduledCount,
    bool IsOneTime = false);

public record RetrospectiveMetrics(
    int CompletionRate,
    int TotalCompletions,
    int TotalScheduled,
    int ActiveDays,
    int PeriodDays,
    int CurrentStreak,
    int BestStreak,
    int BadHabitSlips,
    IReadOnlyList<int> WeeklyConsistency,
    IReadOnlyList<RetrospectiveHabitStat> TopHabits,
    IReadOnlyList<RetrospectiveHabitStat> NeedsAttention);

public record RetrospectiveResponse(
    string Period,
    RetrospectiveMetrics Metrics,
    RetrospectiveNarrative Narrative,
    bool FromCache);

public record GetRetrospectiveQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string Period,
    string Language) : IRequest<Result<RetrospectiveResponse>>;

public class GetRetrospectiveQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IPayGateService payGate,
    IRetrospectiveService retrospectiveService,
    IUserStreakService userStreakService,
    IMemoryCache cache) : IRequestHandler<GetRetrospectiveQuery, Result<RetrospectiveResponse>>
{
    public async Task<Result<RetrospectiveResponse>> Handle(
        GetRetrospectiveQuery request,
        CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanUseRetrospective(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<RetrospectiveResponse>();

        var cacheKey = $"retro:v2:{request.UserId}:{request.Period}:{request.DateFrom}:{request.Language}";

        if (cache.TryGetValue(cacheKey, out RetrospectiveResponse? cached) && cached is not null)
            return Result.Success(cached with { FromCache = true });

        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= request.DateFrom && l.Date <= request.DateTo)),
            cancellationToken);

        var habitList = habits.ToList();

        if (habitList.Count == 0)
            return Result.Failure<RetrospectiveResponse>(ErrorMessages.NoHabitsForPeriod);

        var streakState = await userStreakService.RecalculateAsync(
            request.UserId, cancellationToken, awardFreezeIfEligible: false);

        var metrics = RetrospectiveMetricsCalculator.Compute(
            habitList,
            request.DateFrom,
            request.DateTo,
            streakState?.CurrentStreak ?? 0,
            streakState?.LongestStreak ?? 0);

        if (metrics.TotalCompletions == 0 && metrics.BadHabitSlips == 0)
            return Result.Failure<RetrospectiveResponse>(ErrorMessages.NoHabitsForPeriod);

        var narrativeResult = await retrospectiveService.GenerateRetrospectiveAsync(
            habitList,
            request.DateFrom,
            request.DateTo,
            request.Period,
            request.Language,
            cancellationToken);

        if (narrativeResult.IsFailure)
            return narrativeResult.PropagateError<RetrospectiveResponse>();

        var response = new RetrospectiveResponse(
            request.Period,
            metrics,
            narrativeResult.Value,
            FromCache: false);

        cache.Set(cacheKey, response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        });

        return Result.Success(response);
    }
}
