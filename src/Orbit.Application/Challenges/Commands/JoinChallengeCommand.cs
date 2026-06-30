using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Challenges.Commands;

public record JoinChallengeCommand(
    Guid UserId,
    string Code,
    IReadOnlyList<Guid> LinkedHabitIds) : IRequest<Result>;

public class JoinChallengeCommandHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    IGenericRepository<Challenge> challengeRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<UserAchievement> achievementRepository,
    IXpAwarder xpAwarder,
    IUnitOfWork unitOfWork) : IRequestHandler<JoinChallengeCommand, Result>
{
    private const string TeamPlayerAchievementId = "team_player";

    public async Task<Result> Handle(JoinChallengeCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();
        var user = access.Value;

        var ownedHabits = await VerifyOwnedHabitsAsync(request.UserId, request.LinkedHabitIds, cancellationToken);
        if (ownedHabits.IsFailure)
            return ownedHabits;

        var normalizedCode = request.Code.Trim().ToUpperInvariant();
        var challenge = await challengeRepository.FindOneTrackedAsync(
            c => c.JoinCode == normalizedCode,
            q => q.Include(c => c.Participants).ThenInclude(p => p.LinkedHabits),
            cancellationToken);

        if (challenge is null)
            return Result.Failure(ErrorMessages.InvalidJoinCode);

        if (challenge.Status != ChallengeStatus.Active)
            return Result.Failure(ErrorMessages.ChallengeClosed);

        if (challenge.Participants.Any(p => p.UserId == request.UserId && p.IsActive))
            return Result.Failure(ErrorMessages.AlreadyJoinedChallenge);

        if (await friendGraphService.IsBlockedBetweenAsync(request.UserId, challenge.CreatorId, cancellationToken))
            return Result.Failure(ErrorMessages.InvalidJoinCode);

        if (challenge.GetActiveParticipants().Count >= AppConstants.MaxChallengeParticipants)
            return Result.Failure(ErrorMessages.ChallengeFull.Format(AppConstants.MaxChallengeParticipants));

        challenge.AddParticipant(request.UserId, request.LinkedHabitIds);

        await AwardTeamPlayerAsync(user, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    private async Task<Result> VerifyOwnedHabitsAsync(Guid userId, IReadOnlyList<Guid> habitIds, CancellationToken cancellationToken)
    {
        var distinctIds = habitIds.Distinct().ToList();
        var owned = await habitRepository.CountAsync(
            h => h.UserId == userId && distinctIds.Contains(h.Id),
            cancellationToken);

        return owned == distinctIds.Count
            ? Result.Success()
            : Result.Failure(ErrorMessages.HabitNotFound);
    }

    private async Task AwardTeamPlayerAsync(User user, CancellationToken cancellationToken)
    {
        var alreadyEarned = await achievementRepository.AnyAsync(
            a => a.UserId == user.Id && a.AchievementId == TeamPlayerAchievementId,
            cancellationToken);
        if (alreadyEarned)
            return;

        var earned = new HashSet<string>();
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        AchievementChecks.TryGrant(TeamPlayerAchievementId, user, earned, newAchievements);

        if (newAchievements.Count == 0)
            return;

        await achievementRepository.AddAsync(newAchievements[0].Entity, cancellationToken);
        await xpAwarder.AwardAsync(
            user, newAchievements[0].Definition.XpReward, XpAwardSource.Achievement,
            newAchievements[0].Entity.Id, awardedAtUtc: DateTime.UtcNow, cancellationToken);

        var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
        if (newLevel.Level != user.Level)
            user.SetLevel(newLevel.Level);
    }
}
