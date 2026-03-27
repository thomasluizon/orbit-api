using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class UserAchievement : Entity
{
    public Guid UserId { get; private set; }
    public string AchievementId { get; private set; } = null!;
    public DateTime EarnedAtUtc { get; private set; }

    private UserAchievement() { }

    public static UserAchievement Create(Guid userId, string achievementId)
    {
        return new UserAchievement
        {
            UserId = userId,
            AchievementId = achievementId,
            EarnedAtUtc = DateTime.UtcNow
        };
    }
}
