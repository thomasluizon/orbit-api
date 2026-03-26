using Orbit.Domain.Enums;

namespace Orbit.Application.Gamification.Models;

public record AchievementDefinition(
    string Id,
    string Name,
    string Description,
    AchievementCategory Category,
    AchievementRarity Rarity,
    int XpReward,
    string IconKey);
