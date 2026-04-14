using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserStreakFreezeEarningTests
{
    private static readonly DateOnly Today = new(2026, 4, 15);

    private static User CreateUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public void TryEarnStreakFreezes_CurrentStreakBelow7_NoEarn()
    {
        var user = CreateUser();
        user.SetStreakState(5, 5, Today);

        var outcome = user.TryEarnStreakFreezes();

        outcome.FreezesEarned.Should().Be(0);
        outcome.FreezesCapped.Should().Be(0);
        outcome.NewBalance.Should().Be(0);
        outcome.NewLastEarnedAtStreak.Should().Be(0);
        user.StreakFreezeBalance.Should().Be(0);
    }

    [Fact]
    public void TryEarnStreakFreezes_ExactlyAt7_Earns1()
    {
        var user = CreateUser();
        user.SetStreakState(7, 7, Today);

        var outcome = user.TryEarnStreakFreezes();

        outcome.FreezesEarned.Should().Be(1);
        outcome.FreezesCapped.Should().Be(0);
        outcome.NewBalance.Should().Be(1);
        outcome.NewLastEarnedAtStreak.Should().Be(7);
        user.StreakFreezeBalance.Should().Be(1);
        user.LastFreezeEarnedAtStreak.Should().Be(7);
    }

    [Fact]
    public void TryEarnStreakFreezes_At14_From0_Earns2()
    {
        var user = CreateUser();
        user.SetStreakState(14, 14, Today);

        var outcome = user.TryEarnStreakFreezes();

        outcome.FreezesEarned.Should().Be(2);
        outcome.FreezesCapped.Should().Be(0);
        outcome.NewBalance.Should().Be(2);
        outcome.NewLastEarnedAtStreak.Should().Be(14);
    }

    [Fact]
    public void TryEarnStreakFreezes_AtCapOf3_DoesNotOverflow_ReportsCapped()
    {
        var user = CreateUser();
        user.SetStreakState(35, 35, Today); // 5 eligible freezes

        var outcome = user.TryEarnStreakFreezes();

        outcome.FreezesEarned.Should().Be(3);
        outcome.FreezesCapped.Should().Be(2);
        outcome.NewBalance.Should().Be(3);
        // Anchor advances by the full eligible amount so user does not re-qualify immediately.
        outcome.NewLastEarnedAtStreak.Should().Be(35);
        user.LastFreezeEarnedAtStreak.Should().Be(35);
    }

    [Fact]
    public void TryEarnStreakFreezes_AfterStreakBreak_LastEarnedAtClampedToCurrentStreak()
    {
        var user = CreateUser();
        user.SetStreakState(14, 14, Today);
        user.TryEarnStreakFreezes();
        user.LastFreezeEarnedAtStreak.Should().Be(14);

        // Streak break: current drops to 0.
        user.SetStreakState(0, 14, null);
        user.LastFreezeEarnedAtStreak.Should().Be(0);
    }

    [Fact]
    public void TryEarnStreakFreezes_AdvancesAnchorEvenWhenCapped_NoImmediateRetry()
    {
        var user = CreateUser();
        user.SetStreakState(21, 21, Today);
        user.TryEarnStreakFreezes(); // balance = 3, at cap, anchor = 21
        user.StreakFreezeBalance.Should().Be(3);

        // User consumes one freeze; balance goes to 2.
        user.ConsumeStreakFreeze();
        user.StreakFreezeBalance.Should().Be(2);

        // Log the next day: streak = 22. delta = 1, not enough.
        user.SetStreakState(22, 22, Today);
        var outcome = user.TryEarnStreakFreezes();
        outcome.FreezesEarned.Should().Be(0);
    }

    [Fact]
    public void ConsumeStreakFreeze_FromZeroBalance_ReturnsFailure()
    {
        var user = CreateUser();
        var result = user.ConsumeStreakFreeze();

        result.IsFailure.Should().BeTrue();
        user.StreakFreezeBalance.Should().Be(0);
    }

    [Fact]
    public void ResetAccount_ZeroesBalanceAndAnchor()
    {
        var user = CreateUser();
        user.SetStreakState(21, 21, Today);
        user.TryEarnStreakFreezes();
        user.StreakFreezeBalance.Should().Be(3);
        user.LastFreezeEarnedAtStreak.Should().Be(21);

        user.ResetAccount();

        user.StreakFreezeBalance.Should().Be(0);
        user.LastFreezeEarnedAtStreak.Should().Be(0);
    }

    [Fact]
    public void TryEarnStreakFreezes_SecondEarn_FromNextThreshold()
    {
        var user = CreateUser();
        user.SetStreakState(7, 7, Today);
        user.TryEarnStreakFreezes();
        user.StreakFreezeBalance.Should().Be(1);
        user.LastFreezeEarnedAtStreak.Should().Be(7);

        // Advance to day 14: expect +1.
        user.SetStreakState(14, 14, Today);
        var outcome = user.TryEarnStreakFreezes();
        outcome.FreezesEarned.Should().Be(1);
        user.StreakFreezeBalance.Should().Be(2);
        user.LastFreezeEarnedAtStreak.Should().Be(14);
    }
}
