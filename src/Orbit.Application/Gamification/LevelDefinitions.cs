using Orbit.Application.Gamification.Models;

namespace Orbit.Application.Gamification;

public static class LevelDefinitions
{
    private static readonly List<LevelDefinition> _all =
    [
        new(1, "Starter", 0),
        new(2, "Explorer", 100),
        new(3, "Orbiter", 300),
        new(4, "Navigator", 600),
        new(5, "Pilot", 1_000),
        new(6, "Captain", 1_500),
        new(7, "Commander", 2_500),
        new(8, "Admiral", 4_000),
        new(9, "Elite", 6_000),
        new(10, "Legend", 10_000),
    ];

    public static IReadOnlyList<LevelDefinition> All => _all;

    public static LevelDefinition GetLevelForXp(int totalXp)
    {
        for (var i = _all.Count - 1; i >= 0; i--)
        {
            if (totalXp >= _all[i].XpRequired)
                return _all[i];
        }
        return _all[0];
    }

    public static int? GetXpToNextLevel(int totalXp)
    {
        var current = GetLevelForXp(totalXp);
        if (current.Level >= 10) return null;
        var next = _all[current.Level]; // Level is 1-indexed, array is 0-indexed, so _all[current.Level] = next level
        return next.XpRequired - totalXp;
    }
}
