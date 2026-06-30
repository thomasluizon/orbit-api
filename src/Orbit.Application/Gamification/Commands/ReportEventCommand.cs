using MediatR;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Commands;

public record ReportEventResponse(IReadOnlyList<AchievementDto> Granted);

public record ReportEventCommand(Guid UserId, string EventKey) : IRequest<Result<ReportEventResponse>>;

/// <summary>
/// Maps a whitelisted client event key to its achievement and grants it through the shared idempotent
/// funnel. The key is validated against the whitelist before the handler runs, so a known mapping is
/// guaranteed here. No Pro gate: free users earn these social/sharing badges (display stays Pro-gated).
/// </summary>
public class ReportEventCommandHandler(IGamificationService gamificationService)
    : IRequestHandler<ReportEventCommand, Result<ReportEventResponse>>
{
    public async Task<Result<ReportEventResponse>> Handle(ReportEventCommand request, CancellationToken cancellationToken)
    {
        var achievementId = AchievementEventMap.ClientReportable[request.EventKey];

        var grantedIds = await gamificationService.TryGrantAchievementsAsync(
            request.UserId, [achievementId], cancellationToken);

        var granted = grantedIds
            .Select(AchievementDefinitions.GetById)
            .Where(definition => definition is not null)
            .Select(definition => new AchievementDto(
                definition!.Id,
                definition.Name,
                definition.Description,
                definition.Category.ToString(),
                definition.Rarity.ToString(),
                definition.XpReward,
                definition.IconKey,
                IsEarned: true,
                EarnedAtUtc: DateTime.UtcNow))
            .ToList();

        return Result.Success(new ReportEventResponse(granted));
    }
}
