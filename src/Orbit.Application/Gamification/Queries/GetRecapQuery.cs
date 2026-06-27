using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Queries;

public record RecapResponse(
    string Period,
    RetrospectiveMetrics Metrics,
    string ShareDeepLink);

public record GetRecapQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string Period) : IRequest<Result<RecapResponse>>;

/// <summary>
/// Builds a shareable, metrics-only recap for the given period by reusing
/// <see cref="RetrospectiveMetricsCalculator"/> (no AI narrative). Free / ungated. Ensures the
/// user has a referral code (generating one if missing) so the returned <c>ShareDeepLink</c> can
/// carry it for attribution.
/// </summary>
public class GetRecapQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IUserStreakService userStreakService,
    IOptions<FrontendSettings> frontendSettings,
    IMediator mediator) : IRequestHandler<GetRecapQuery, Result<RecapResponse>>
{
    public async Task<Result<RecapResponse>> Handle(GetRecapQuery request, CancellationToken cancellationToken)
    {
        var codeResult = await mediator.Send(new GetOrCreateReferralCodeCommand(request.UserId), cancellationToken);
        if (!codeResult.IsSuccess)
            return codeResult.PropagateError<RecapResponse>();

        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= request.DateFrom && l.Date <= request.DateTo)),
            cancellationToken);

        var streakState = await userStreakService.RecalculateAsync(
            request.UserId, cancellationToken, awardFreezeIfEligible: false);

        var metrics = RetrospectiveMetricsCalculator.Compute(
            habits.ToList(),
            request.DateFrom,
            request.DateTo,
            streakState?.CurrentStreak ?? 0,
            streakState?.LongestStreak ?? 0);

        var shareDeepLink = $"{frontendSettings.Value.BaseUrl}/r/{codeResult.Value}?recap={request.Period}";

        return Result.Success(new RecapResponse(request.Period, metrics, shareDeepLink));
    }
}
