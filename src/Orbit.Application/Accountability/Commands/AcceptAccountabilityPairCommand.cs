using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Commands;

public record AcceptAccountabilityPairCommand(
    Guid UserId,
    Guid PairId,
    IReadOnlyList<Guid> HabitIds) : IRequest<Result>;

public partial class AcceptAccountabilityPairCommandHandler(
    SocialAccessGuard socialAccessGuard,
    AccountabilityPairService accountabilityPairService,
    AccountabilityRepositories repositories,
    IXpAwarder xpAwarder,
    IPushNotificationService pushNotificationService,
    IUnitOfWork unitOfWork,
    ILogger<AcceptAccountabilityPairCommandHandler> logger) : IRequestHandler<AcceptAccountabilityPairCommand, Result>
{
    private const string BattleBuddyAchievementId = "battle_buddy";

    public async Task<Result> Handle(AcceptAccountabilityPairCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();
        var accepter = access.Value;

        var pair = await repositories.Pairs.FindOneTrackedAsync(
            p => p.Id == request.PairId && p.AddresseeId == request.UserId,
            cancellationToken: cancellationToken);
        if (pair is null)
            return Result.Failure(ErrorMessages.PairNotFound);

        var acceptResult = pair.Accept();
        if (acceptResult.IsFailure)
            return acceptResult;

        var linkResult = await accountabilityPairService.ReplaceLinkedHabitsAsync(
            pair, request.UserId, request.HabitIds, cancellationToken);
        if (linkResult.IsFailure)
            return linkResult;

        var requester = await repositories.Users.FindOneTrackedAsync(
            u => u.Id == pair.RequesterId,
            cancellationToken: cancellationToken);

        await AwardBattleBuddyAsync(accepter, cancellationToken);
        if (requester is not null)
            await AwardBattleBuddyAsync(requester, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await NotifyRequesterAsync(requester, accepter, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Idempotently grants the Battle Buddy achievement (and its XP reward) to a pair participant on accept.
    /// </summary>
    private async Task AwardBattleBuddyAsync(User user, CancellationToken cancellationToken)
    {
        var alreadyEarned = await repositories.Achievements.AnyAsync(
            a => a.UserId == user.Id && a.AchievementId == BattleBuddyAchievementId,
            cancellationToken);
        if (alreadyEarned)
            return;

        var earned = new HashSet<string>();
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        AchievementChecks.TryGrant(BattleBuddyAchievementId, user, earned, newAchievements);

        if (newAchievements.Count == 0)
            return;

        await repositories.Achievements.AddAsync(newAchievements[0].Entity, cancellationToken);
        await xpAwarder.AwardAsync(
            user, newAchievements[0].Definition.XpReward, XpAwardSource.Achievement,
            newAchievements[0].Entity.Id, awardedAtUtc: DateTime.UtcNow, cancellationToken);

        var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
        if (newLevel.Level != user.Level)
            user.SetLevel(newLevel.Level);
    }

    private async Task NotifyRequesterAsync(User? requester, User accepter, CancellationToken cancellationToken)
    {
        if (requester is null || !requester.SocialOptIn)
            return;

        var isPortuguese = LocaleHelper.IsPortuguese(requester.Language);
        var title = isPortuguese ? "Parceria aceita" : "Accountability invite accepted";
        var body = isPortuguese
            ? $"{accepter.Name} aceitou seu convite de parceria."
            : $"{accepter.Name} accepted your accountability invite.";

        try
        {
            await pushNotificationService.SendToUserAsync(requester.Id, title, body, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            LogPushNotificationFailed(logger, ex, requester.Id);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Accountability accept push failed for user {UserId}")]
    private static partial void LogPushNotificationFailed(ILogger logger, Exception ex, Guid userId);
}
