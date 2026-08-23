using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Gamification.Queries;

public record RecapResponse(
    string Period,
    RetrospectiveMetrics Metrics,
    string ShareDeepLink,
    int GoalCompletions = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateOnly? DateFrom = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateOnly? DateTo = null);

public record GetRecapQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string Period,
    int? ClosedYear = null,
    int? ClosedMonth = null) : IRequest<Result<RecapResponse>>;

/// <summary>
/// Builds a shareable, metrics-only recap for the given period by reusing
/// <see cref="RetrospectiveMetricsCalculator"/> (no AI narrative). Free / ungated. Ensures the
/// user has a referral code (generating one if missing) so the returned <c>ShareDeepLink</c> can
/// carry it for attribution.
/// </summary>
public class GetRecapQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<User> userRepository,
    IUserStreakService userStreakService,
    IOptions<FrontendSettings> frontendSettings,
    IMediator mediator,
    IMemoryCache cache) : IRequestHandler<GetRecapQuery, Result<RecapResponse>>
{
    public async Task<Result<RecapResponse>> Handle(GetRecapQuery request, CancellationToken cancellationToken)
    {
        var isClosedMonth = request.ClosedYear.HasValue && request.ClosedMonth.HasValue;
        var cacheKey = isClosedMonth
            ? $"recap:closed:{request.UserId}:{request.DateFrom:yyyy-MM-dd}:{request.DateTo:yyyy-MM-dd}"
            : null;

        if (cacheKey is not null && cache.TryGetValue(cacheKey, out RecapResponse? cached) && cached is not null)
            return Result.Success(cached);

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<RecapResponse>(ErrorMessages.UserNotFound);

        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone);
        if (isClosedMonth && IsBeforeAccountMonth(request.DateFrom, user, userTimeZone))
            return Result.Failure<RecapResponse>(ErrorMessages.RecapMonthBeforeAccount);

        var codeResult = await mediator.Send(new GetOrCreateReferralCodeCommand(request.UserId), cancellationToken);
        if (!codeResult.IsSuccess)
            return codeResult.PropagateError<RecapResponse>();

        var habits = await LoadHabitsAsync(request, isClosedMonth, cancellationToken);

        var streakState = await userStreakService.RecalculateAsync(
            request.UserId, awardFreezeIfEligible: false, cancellationToken);

        var metrics = ComputeMetrics(request, habits, streakState, userTimeZone, isClosedMonth);
        var goalCompletions = await CountGoalCompletionsAsync(request, userTimeZone, cancellationToken);

        var shareDeepLink = isClosedMonth
            ? $"{frontendSettings.Value.BaseUrl}/r/{codeResult.Value}?recap={request.Period}&year={request.ClosedYear}&month={request.ClosedMonth}"
            : $"{frontendSettings.Value.BaseUrl}/r/{codeResult.Value}?recap={request.Period}";

        var response = new RecapResponse(
            request.Period,
            metrics,
            shareDeepLink,
            goalCompletions,
            isClosedMonth ? request.DateFrom : null,
            isClosedMonth ? request.DateTo : null);

        if (cacheKey is not null)
            cache.Set(cacheKey, response);

        return Result.Success(response);
    }

    private static bool IsBeforeAccountMonth(DateOnly dateFrom, User user, TimeZoneInfo userTimeZone)
    {
        var accountCreatedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(user.CreatedAtUtc, DateTimeKind.Utc),
            userTimeZone);
        var accountFirstMonth = new DateOnly(accountCreatedLocal.Year, accountCreatedLocal.Month, 1);
        return dateFrom < accountFirstMonth;
    }

    private async Task<IReadOnlyList<Habit>> LoadHabitsAsync(
        GetRecapQuery request,
        bool isClosedMonth,
        CancellationToken cancellationToken)
    {
        Func<IQueryable<Habit>, IQueryable<Habit>> includePeriodLogs =
            q => q.Include(h => h.Logs.Where(l => l.Date >= request.DateFrom && l.Date <= request.DateTo));
        return isClosedMonth
            ? await habitRepository.FindIgnoringFiltersAsync(
                h => h.UserId == request.UserId,
                includePeriodLogs,
                cancellationToken)
            : await habitRepository.FindAsync(
                h => h.UserId == request.UserId,
                includePeriodLogs,
                cancellationToken);
    }

    private static RetrospectiveMetrics ComputeMetrics(
        GetRecapQuery request,
        IReadOnlyList<Habit> habits,
        UserStreakState? streakState,
        TimeZoneInfo userTimeZone,
        bool isClosedMonth)
    {
        return isClosedMonth
            ? RetrospectiveMetricsCalculator.ComputeHistorical(
                habits.ToList(),
                request.DateFrom,
                request.DateTo,
                streakState?.CurrentStreak ?? 0,
                streakState?.LongestStreak ?? 0,
                userTimeZone)
            : RetrospectiveMetricsCalculator.Compute(
                habits.ToList(),
                request.DateFrom,
                request.DateTo,
                streakState?.CurrentStreak ?? 0,
                streakState?.LongestStreak ?? 0);
    }

    private async Task<int> CountGoalCompletionsAsync(
        GetRecapQuery request,
        TimeZoneInfo userTimeZone,
        CancellationToken cancellationToken)
    {
        var dateFromUtc = ToUtcStart(request.DateFrom, userTimeZone);
        var dateToExclusiveUtc = ToUtcStart(request.DateTo.AddDays(1), userTimeZone);
        return await goalRepository.CountAsync(
            goal => goal.UserId == request.UserId
                && !goal.IsDeleted
                && goal.CompletedAtUtc.HasValue
                && goal.CompletedAtUtc.Value >= dateFromUtc
                && goal.CompletedAtUtc.Value < dateToExclusiveUtc,
            cancellationToken);
    }

    private static DateTime ToUtcStart(DateOnly date, TimeZoneInfo userTimeZone)
    {
        var localStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localStart, userTimeZone);
    }
}
