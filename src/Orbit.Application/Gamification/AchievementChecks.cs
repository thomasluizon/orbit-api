using Orbit.Application.Gamification.Models;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;

namespace Orbit.Application.Gamification;

/// <summary>
/// Pure achievement-evaluation rules shared by <see cref="Services.GamificationService"/>: given the
/// already-loaded user, earned-achievement set, and habit context, each check records any newly
/// qualifying achievement into <c>newAchievements</c>. The caller awards each achievement's XP through
/// the audited funnel when persisting. No I/O, no injected dependencies — every input is passed in.
/// </summary>
public static class AchievementChecks
{
    public const int PerfectStreakWindowDays = 30;

    /// <summary>
    /// The single source of truth for a linear-threshold achievement: its <see cref="AchievementDefinition.ProgressTarget"/>.
    /// Throws if the id is unknown or the achievement has no target, since every caller here passes a
    /// compile-time-known quantifiable achievement id — a miss is a programming error, not a runtime state.
    /// </summary>
    public static int TargetFor(string achievementId) =>
        AchievementDefinitions.GetById(achievementId)?.ProgressTarget
            ?? throw new InvalidOperationException($"Achievement '{achievementId}' has no ProgressTarget");

    public static void TryGrant(
        string achievementId,
        User user,
        HashSet<string> earned,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        if (earned.Contains(achievementId)) return;

        var definition = AchievementDefinitions.GetById(achievementId);
        if (definition is null) return;

        var entity = UserAchievement.Create(user.Id, achievementId);
        earned.Add(achievementId);
        newAchievements.Add((entity, definition));
    }

    public static void CheckOnboardingChecklist(
        User user,
        HashSet<string> earned,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        if (user.HasCreatedFirstHabit && user.HasLoggedFirstHabit && user.HasTriedAstra)
            TryGrant(AchievementDefinitions.OnboardingComplete, user, earned, newAchievements);
    }

    public static void CheckConsistencyAchievements(
        int currentStreak,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        if (currentStreak >= TargetFor(AchievementDefinitions.WeekWarrior))
            TryGrant(AchievementDefinitions.WeekWarrior, user, earned, newAchievements);
        if (currentStreak >= TargetFor(AchievementDefinitions.FortnightFocus))
            TryGrant(AchievementDefinitions.FortnightFocus, user, earned, newAchievements);
        if (currentStreak >= TargetFor(AchievementDefinitions.MonthlyMaster))
            TryGrant(AchievementDefinitions.MonthlyMaster, user, earned, newAchievements);
        if (currentStreak >= TargetFor(AchievementDefinitions.QuarterChampion))
            TryGrant(AchievementDefinitions.QuarterChampion, user, earned, newAchievements);
        if (currentStreak >= TargetFor(AchievementDefinitions.Centurion))
            TryGrant(AchievementDefinitions.Centurion, user, earned, newAchievements);
        if (currentStreak >= TargetFor(AchievementDefinitions.HalfYearHero))
            TryGrant(AchievementDefinitions.HalfYearHero, user, earned, newAchievements);
        if (currentStreak >= TargetFor(AchievementDefinitions.YearOfDiscipline))
            TryGrant(AchievementDefinitions.YearOfDiscipline, user, earned, newAchievements);
        if (currentStreak >= TargetFor(AchievementDefinitions.StreakTitan))
            TryGrant(AchievementDefinitions.StreakTitan, user, earned, newAchievements);
        if (currentStreak >= TargetFor(AchievementDefinitions.StreakImmortal))
            TryGrant(AchievementDefinitions.StreakImmortal, user, earned, newAchievements);
    }

    public static void CheckVolumeAchievements(
        int totalCompletions,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        if (totalCompletions >= TargetFor(AchievementDefinitions.GettingMomentum))
            TryGrant(AchievementDefinitions.GettingMomentum, user, earned, newAchievements);
        if (totalCompletions >= TargetFor(AchievementDefinitions.BuildingHabits))
            TryGrant(AchievementDefinitions.BuildingHabits, user, earned, newAchievements);
        if (totalCompletions >= TargetFor(AchievementDefinitions.Dedicated))
            TryGrant(AchievementDefinitions.Dedicated, user, earned, newAchievements);
        if (totalCompletions >= TargetFor(AchievementDefinitions.Relentless))
            TryGrant(AchievementDefinitions.Relentless, user, earned, newAchievements);
        if (totalCompletions >= TargetFor(AchievementDefinitions.LegendaryVolume))
            TryGrant(AchievementDefinitions.LegendaryVolume, user, earned, newAchievements);
        if (totalCompletions >= TargetFor(AchievementDefinitions.Unstoppable))
            TryGrant(AchievementDefinitions.Unstoppable, user, earned, newAchievements);
    }

    public static void CheckPerfectDay(
        IReadOnlyList<Habit> allUserHabits,
        DateOnly today,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        if (earned.Contains(AchievementDefinitions.PerfectDay)) return;

        var eligibleHabits = allUserHabits
            .Where(h => !h.IsCompleted && !h.IsGeneral && h.ParentHabitId == null)
            .ToList();

        if (eligibleHabits.Count == 0) return;

        var scheduledToday = eligibleHabits.Where(h => HabitScheduleService.IsHabitDueOnDate(h, today)).ToList();
        if (scheduledToday.Count == 0) return;

        var allDone = scheduledToday.All(h => h.Logs.Any(l => l.Date == today));
        if (allDone)
            TryGrant(AchievementDefinitions.PerfectDay, user, earned, newAchievements);
    }

    public static void CheckPerfectWeekAndMonth(
        IReadOnlyList<Habit> allUserHabits,
        DateOnly today,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        var eligibleHabits = allUserHabits
            .Where(h => !h.IsCompleted && !h.IsGeneral && h.ParentHabitId == null)
            .ToList();

        if (eligibleHabits.Count == 0) return;

        var consecutivePerfectDays = 0;
        for (var day = today; day >= today.AddDays(-PerfectStreakWindowDays); day = day.AddDays(-1))
        {
            var scheduledForDay = eligibleHabits.Where(h => HabitScheduleService.IsHabitDueOnDate(h, day)).ToList();
            if (scheduledForDay.Count == 0)
            {
                if (day != today) consecutivePerfectDays++;
                continue;
            }

            var allDone = scheduledForDay.All(h => h.Logs.Any(l => l.Date == day));
            if (!allDone) break;

            consecutivePerfectDays++;
        }

        if (consecutivePerfectDays >= 7 && !earned.Contains(AchievementDefinitions.PerfectWeek))
            TryGrant(AchievementDefinitions.PerfectWeek, user, earned, newAchievements);

        if (consecutivePerfectDays >= 30 && !earned.Contains(AchievementDefinitions.PerfectMonth))
            TryGrant(AchievementDefinitions.PerfectMonth, user, earned, newAchievements);
    }

    public static void CheckTimeBasedAchievements(
        User user,
        HashSet<string> earned,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements,
        IReadOnlyList<HabitLog> logsWithRecentCreationTimes,
        TimeZoneInfo userTz)
    {
        var checkEarly = !earned.Contains(AchievementDefinitions.EarlyBird);
        var checkNight = !earned.Contains(AchievementDefinitions.NightOwl);
        if (!checkEarly && !checkNight) return;

        if (checkEarly)
        {
            var earlyCount = logsWithRecentCreationTimes.Count(l =>
            {
                var userTime = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, userTz);
                return userTime.Hour < 7;
            });
            if (earlyCount >= TargetFor(AchievementDefinitions.EarlyBird))
                TryGrant(AchievementDefinitions.EarlyBird, user, earned, newAchievements);
        }

        if (checkNight)
        {
            var nightCount = logsWithRecentCreationTimes.Count(l =>
            {
                var userTime = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, userTz);
                return userTime.Hour >= 22;
            });
            if (nightCount >= TargetFor(AchievementDefinitions.NightOwl))
                TryGrant(AchievementDefinitions.NightOwl, user, earned, newAchievements);
        }
    }
}
