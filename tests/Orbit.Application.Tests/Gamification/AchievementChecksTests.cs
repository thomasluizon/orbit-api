using FluentAssertions;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Models;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Gamification;

public class AchievementChecksTests
{
    private static User CreateUser() => User.Create("Test User", "test@example.com").Value;

    private static List<(UserAchievement Entity, AchievementDefinition Definition)> NewList() => [];

    [Fact]
    public void CheckConsistencyAchievements_1000DayStreak_GrantsStreakImmortal()
    {
        var user = CreateUser();
        var earned = new HashSet<string>();
        var newAchievements = NewList();

        AchievementChecks.CheckConsistencyAchievements(1000, earned, user, newAchievements);

        newAchievements.Should().Contain(a => a.Definition.Id == AchievementDefinitions.StreakImmortal);
    }

    [Fact]
    public void CheckConsistencyAchievements_999DayStreak_DoesNotGrantStreakImmortal()
    {
        var user = CreateUser();
        var earned = new HashSet<string>();
        var newAchievements = NewList();

        AchievementChecks.CheckConsistencyAchievements(999, earned, user, newAchievements);

        newAchievements.Should().NotContain(a => a.Definition.Id == AchievementDefinitions.StreakImmortal);
    }

    [Fact]
    public void CheckVolumeAchievements_2500Completions_GrantsUnstoppable()
    {
        var user = CreateUser();
        var earned = new HashSet<string>();
        var newAchievements = NewList();

        AchievementChecks.CheckVolumeAchievements(2500, earned, user, newAchievements);

        newAchievements.Should().Contain(a => a.Definition.Id == AchievementDefinitions.Unstoppable);
    }

    [Fact]
    public void CheckVolumeAchievements_2499Completions_DoesNotGrantUnstoppable()
    {
        var user = CreateUser();
        var earned = new HashSet<string>();
        var newAchievements = NewList();

        AchievementChecks.CheckVolumeAchievements(2499, earned, user, newAchievements);

        newAchievements.Should().NotContain(a => a.Definition.Id == AchievementDefinitions.Unstoppable);
    }

    [Theory]
    [InlineData(AchievementDefinitions.WeekWarrior)]
    [InlineData(AchievementDefinitions.MonthlyMaster)]
    [InlineData(AchievementDefinitions.QuarterChampion)]
    [InlineData(AchievementDefinitions.StreakTitan)]
    public void CheckConsistencyAchievements_GrantsExactlyAtDefinitionTarget(string achievementId)
    {
        var target = AchievementDefinitions.GetById(achievementId)!.ProgressTarget!.Value;

        var atTarget = NewList();
        AchievementChecks.CheckConsistencyAchievements(target, new HashSet<string>(), CreateUser(), atTarget);
        atTarget.Should().Contain(a => a.Definition.Id == achievementId);

        var belowTarget = NewList();
        AchievementChecks.CheckConsistencyAchievements(target - 1, new HashSet<string>(), CreateUser(), belowTarget);
        belowTarget.Should().NotContain(a => a.Definition.Id == achievementId);
    }

    [Theory]
    [InlineData(AchievementDefinitions.GettingMomentum)]
    [InlineData(AchievementDefinitions.Dedicated)]
    [InlineData(AchievementDefinitions.Relentless)]
    public void CheckVolumeAchievements_GrantsExactlyAtDefinitionTarget(string achievementId)
    {
        var target = AchievementDefinitions.GetById(achievementId)!.ProgressTarget!.Value;

        var atTarget = NewList();
        AchievementChecks.CheckVolumeAchievements(target, new HashSet<string>(), CreateUser(), atTarget);
        atTarget.Should().Contain(a => a.Definition.Id == achievementId);

        var belowTarget = NewList();
        AchievementChecks.CheckVolumeAchievements(target - 1, new HashSet<string>(), CreateUser(), belowTarget);
        belowTarget.Should().NotContain(a => a.Definition.Id == achievementId);
    }
}
