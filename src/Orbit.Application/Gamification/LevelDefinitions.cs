using Orbit.Application.Gamification.Models;
using Orbit.Domain.Entities;

namespace Orbit.Application.Gamification;

/// <summary>
/// The level ladder. Levels 1–10 use the hand-tuned anchor table; levels past 10 follow the
/// steady-climb quadratic <c>XpRequired(L) = 100·L²</c>, which is value-continuous with the table
/// at level 10 (both equal 10,000) and grows forever, so there is no level cap. Titles past 10
/// reuse level 10's "Legend"; the numeric level differentiates.
/// </summary>
public static class LevelDefinitions
{
    public const int TableMaxLevel = 10;
    private const int QuadraticXpCoefficient = 100;
    private const string LegendTitle = "Legend";

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

    /// <summary>
    /// Total XP required to reach <paramref name="level"/>. Levels at or below the anchor table use
    /// its thresholds; past it, <c>100·level²</c> (which equals the table at level 10, so the curve
    /// is continuous). Levels below 1 require 0 XP.
    /// </summary>
    public static int XpRequiredForLevel(int level)
    {
        if (level <= 1) return 0;
        if (level <= TableMaxLevel) return _all[level - 1].XpRequired;
        return QuadraticXpCoefficient * level * level;
    }

    /// <summary>
    /// The display title for <paramref name="level"/>: the anchor table's title up to level 10,
    /// then "Legend" for every level beyond.
    /// </summary>
    public static string TitleForLevel(int level)
    {
        if (level < 1) return _all[0].Title;
        if (level <= TableMaxLevel) return _all[level - 1].Title;
        return LegendTitle;
    }

    /// <summary>
    /// Stable, locale-independent key for <paramref name="level"/>'s title (e.g. "explorer"),
    /// for clients to localize instead of displaying <see cref="TitleForLevel"/>'s English literal.
    /// </summary>
    public static string TitleKeyForLevel(int level) => TitleForLevel(level).ToLowerInvariant();

    public static LevelDefinition GetLevelForXp(int totalXp)
    {
        if (totalXp < _all[TableMaxLevel - 1].XpRequired)
        {
            for (var i = _all.Count - 1; i >= 0; i--)
            {
                if (totalXp >= _all[i].XpRequired)
                    return _all[i];
            }
            return _all[0];
        }

        var level = (int)Math.Floor(Math.Sqrt(totalXp / (double)QuadraticXpCoefficient));
        if (level < TableMaxLevel) level = TableMaxLevel;
        while (XpRequiredForLevel(level + 1) <= totalXp) level++;
        while (XpRequiredForLevel(level) > totalXp) level--;
        return new LevelDefinition(level, TitleForLevel(level), XpRequiredForLevel(level));
    }

    /// <summary>
    /// Recomputes <paramref name="user"/>'s level from their current <see cref="User.TotalXp"/> and
    /// applies it when it changed. The single level-sync every XP-awarding flow calls after granting XP.
    /// </summary>
    public static void SyncLevel(User user)
    {
        var newLevel = GetLevelForXp(user.TotalXp);
        if (newLevel.Level != user.Level)
            user.SetLevel(newLevel.Level);
    }

    /// <summary>
    /// XP remaining until the next level. With infinite levels this is never null — the curve always
    /// has a next threshold.
    /// </summary>
    public static int GetXpToNextLevel(int totalXp)
    {
        var current = GetLevelForXp(totalXp);
        return XpRequiredForLevel(current.Level + 1) - totalXp;
    }
}
