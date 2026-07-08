using FluentAssertions;
using FsCheck.Xunit;
using Orbit.Domain.Entities;
using Orbit.Tests.Generators;

namespace Orbit.Domain.Tests.Entities;

[Properties(Arbitrary = new[] { typeof(OrbitArbitraries) }, MaxTest = 100, Replay = "(20000003,20000009)")]
public class UserGamificationPropertyTests
{
    private static User NewUser() => User.Create("Property User", "property@example.com").Value;

    [Property]
    public void AddXp_IsMonotonic_AndSumsPositiveAmounts(int[] rawAmounts)
    {
        var user = NewUser();
        var amounts = rawAmounts.Select(amount => amount % 100_001).ToArray();
        var previous = user.TotalXp;
        var expected = 0;

        foreach (var amount in amounts)
        {
            user.AddXp(amount);
            user.TotalXp.Should().BeGreaterThanOrEqualTo(previous);
            previous = user.TotalXp;
            if (amount > 0) expected += amount;
        }

        user.TotalXp.Should().Be(expected);
    }

    [Property]
    public void SetLevel_NeverGoesBelowOne(int rawLevel)
    {
        var user = NewUser();
        var level = rawLevel % 10_000;

        user.SetLevel(level);

        user.Level.Should().BeGreaterThanOrEqualTo(1);
        user.Level.Should().Be(level >= 1 ? level : 1);
    }

    [Property]
    public void UpdateStreak_ConsecutiveDays_IncrementsByExactlyOne(DateOnly start, RunLength runLength)
    {
        var user = NewUser();
        var days = runLength.Value;

        for (var offset = 0; offset < days; offset++)
            user.UpdateStreak(start.AddDays(offset));

        user.CurrentStreak.Should().Be(days);
        user.LongestStreak.Should().Be(days);
    }

    [Property]
    public void UpdateStreak_LongestNeverBelowCurrent(DateOnly[] dates)
    {
        var user = NewUser();

        foreach (var date in dates)
        {
            user.UpdateStreak(date);
            user.LongestStreak.Should().BeGreaterThanOrEqualTo(user.CurrentStreak);
            user.CurrentStreak.Should().BeGreaterThanOrEqualTo(1);
        }
    }

    [Property]
    public void AwardStreakFreeze_StaysBoundedAndMonotonic(int[] rawStreaks)
    {
        var user = NewUser();
        var streaks = rawStreaks.Select(streak => Math.Abs(streak % 501)).ToArray();
        var previousFreezes = user.StreakFreezesAccumulated;

        foreach (var streak in streaks)
        {
            user.SetStreakState(streak, streak, null);
            user.AwardStreakFreezeIfEligible();

            user.StreakFreezesAccumulated.Should().BeInRange(0, 3);
            user.StreakFreezesAccumulated.Should().BeGreaterThanOrEqualTo(previousFreezes);
            previousFreezes = user.StreakFreezesAccumulated;
        }
    }
}
