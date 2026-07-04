using Orbit.Application.Gamification;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Queries;

/// <summary>
/// Projects a user's earned achievements into the <see cref="PublicAchievement"/> shape shared by
/// the public profile and the friend profile, newest first, skipping ids without a definition.
/// </summary>
public static class PublicAchievementsBuilder
{
    public static async Task<IReadOnlyList<PublicAchievement>> BuildAsync(
        IGenericRepository<UserAchievement> achievementRepository,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var earned = await achievementRepository.FindAsync(a => a.UserId == userId, cancellationToken);

        return earned
            .OrderByDescending(a => a.EarnedAtUtc)
            .Select(a => AchievementDefinitions.GetById(a.AchievementId))
            .Where(def => def is not null)
            .Select(def => new PublicAchievement(def!.Name, def.IconKey, def.Rarity.ToString()))
            .ToList();
    }
}
