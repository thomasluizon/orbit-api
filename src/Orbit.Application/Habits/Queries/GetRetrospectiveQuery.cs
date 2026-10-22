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
    bool IsOneTime = false,
    Guid? HabitId = null);

/// <summary>Structured habit metrics for a retrospective or recap.</summary>
/// <param name="WeeklyConsistency">Seven weekday percentages ordered Monday through Sunday.</param>
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
    IReadOnlyList<RetrospectiveHabitStat> NeedsAttention,
    CompletionSeries? CompletionSeries = null);

public record CompletionSeries(string Granularity, IReadOnlyList<CompletionSeriesPoint> Points);

public record CompletionSeriesPoint(
    DateOnly StartDate,
    DateOnly EndDate,
    int Scheduled,
    int Completed,
    int? CompletionRate);

public record RetrospectiveResponse(
    string Period,
    RetrospectiveMetrics Metrics,
    RetrospectiveNarrative Narrative,
    bool FromCache,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    string? SourceRevision = null);

public record GetRetrospectiveQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string Period,
    string Language, bool GenerateNarrative = true) : IRequest<Result<RetrospectiveResponse>>;

public class GetRetrospectiveQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IPayGateService payGate,
    IRetrospectiveService retrospectiveService,
    IUserStreakService userStreakService,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<GetRetrospectiveQuery, Result<RetrospectiveResponse>>
{
    public async Task<Result<RetrospectiveResponse>> Handle(
        GetRetrospectiveQuery request,
        CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanUseRetrospective(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<RetrospectiveResponse>();

        var cacheKey = RetrospectiveCacheKey.Build(
            request.UserId,
            request.Period,
            request.DateFrom,
            request.Language);

        if (request.GenerateNarrative && cache.TryGetValue(cacheKey, out RetrospectiveResponse? cached) && cached is not null)
            return Result.Success(cached with { FromCache = true });

        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= request.DateFrom && l.Date <= request.DateTo)),
            cancellationToken);

        var habitList = habits.ToList();

        if (habitList.Count == 0)
            return Result.Failure<RetrospectiveResponse>(ErrorMessages.NoHabitsForPeriod);

        var streakState = await userStreakService.CalculateAsync(
            request.UserId, cancellationToken);
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);

        var metrics = RetrospectiveMetricsCalculator.Compute(
            habitList,
            request.DateFrom,
            request.DateTo,
            streakState?.CurrentStreak ?? 0,
            streakState?.LongestStreak ?? 0,
            weekStartDay);

        if (metrics.TotalCompletions == 0 && metrics.BadHabitSlips == 0)
            return Result.Failure<RetrospectiveResponse>(ErrorMessages.NoHabitsForPeriod);

        if (!request.GenerateNarrative)
            return Result.Success(new RetrospectiveResponse(request.Period, metrics,
                new RetrospectiveNarrative(string.Empty, string.Empty, string.Empty, string.Empty), false,
                request.DateFrom, request.DateTo, Orbit.Application.Chat.ReadRevision.Hash(new
                {
                    metrics,
                    Sources = habitList.OrderBy(h => h.Id).Select(h => new
                    {
                        h.Id,
                        h.UpdatedAtUtc,
                        Logs = h.Logs.Where(l => !l.IsDeleted).OrderBy(l => l.Id).Select(l => new { l.Id, l.Date, l.Value, l.UpdatedAtUtc })
                    })
                })));

        var narrativeResult = await retrospectiveService.GenerateRetrospectiveAsync(
            habitList,
            request.DateFrom,
            request.DateTo,
            request.Period,
            request.Language,
            cancellationToken,
            weekStartDay);

        if (narrativeResult.IsFailure)
            return narrativeResult.PropagateError<RetrospectiveResponse>();

        var response = new RetrospectiveResponse(
            request.Period,
            metrics,
            narrativeResult.Value,
            FromCache: false,
            DateFrom: request.DateFrom,
            DateTo: request.DateTo, SourceRevision: Revision(habitList));

        cache.Set(cacheKey, response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        });

        return Result.Success(response);
    }
    private static string Revision(IReadOnlyList<Habit> habits) => Orbit.Application.Chat.ReadRevision.Hash(
        habits.OrderBy(h => h.Id).Select(h => new
        {
            h.Id,
            h.UpdatedAtUtc,
            Logs = h.Logs.Where(l => !l.IsDeleted).OrderBy(l => l.Id).Select(l => new { l.Id, l.Date, l.Value, l.UpdatedAtUtc })
        }));

}
