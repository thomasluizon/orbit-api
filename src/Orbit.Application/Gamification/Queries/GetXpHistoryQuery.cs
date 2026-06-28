using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Queries;

public record XpHistoryPoint(DateOnly Date, int DailyXp, int CumulativeXp);

public record XpHistoryResponse(int TotalXp, IReadOnlyList<XpHistoryPoint> Points);

public record GetXpHistoryQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo) : IRequest<Result<XpHistoryResponse>>;

/// <summary>
/// Builds the user's cumulative XP-over-time curve across a date range by aggregating
/// <see cref="XpAwardLog"/> rows per day. Carries the pre-range total forward as the starting baseline
/// so the cumulative line is continuous; reports the user's live <c>TotalXp</c> alongside. Pro-gated
/// like the gamification surfaces.
/// </summary>
public class GetXpHistoryQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<XpAwardLog> xpAwardLogRepository,
    IFeatureFlagService featureFlagService) : IRequestHandler<GetXpHistoryQuery, Result<XpHistoryResponse>>
{
    public async Task<Result<XpHistoryResponse>> Handle(GetXpHistoryQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<XpHistoryResponse>(ErrorMessages.UserNotFound);

        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
        var unlocked = user.HasProAccess || enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier);
        if (!unlocked)
            return Result.PayGateFailure<XpHistoryResponse>("XP insights are a Pro feature. Upgrade to unlock!");

        var fromUtc = request.DateFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toExclusiveUtc = request.DateTo.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var rows = await xpAwardLogRepository.FindAsync(
            x => x.UserId == request.UserId && x.AwardedAtUtc < toExclusiveUtc,
            cancellationToken);

        var baseline = rows.Where(r => r.AwardedAtUtc < fromUtc).Sum(r => r.Amount);
        var dailyByDate = rows
            .Where(r => r.AwardedAtUtc >= fromUtc)
            .GroupBy(r => DateOnly.FromDateTime(r.AwardedAtUtc))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        var points = new List<XpHistoryPoint>();
        var cumulative = baseline;
        for (var date = request.DateFrom; date <= request.DateTo; date = date.AddDays(1))
        {
            var daily = dailyByDate.GetValueOrDefault(date, 0);
            cumulative += daily;
            points.Add(new XpHistoryPoint(date, daily, cumulative));
        }

        return Result.Success(new XpHistoryResponse(user.TotalXp, points));
    }
}
