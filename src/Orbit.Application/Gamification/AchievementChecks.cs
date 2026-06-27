using Orbit.Application.Gamification.Models;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;

namespace Orbit.Application.Gamification;

/// <summary>
/// Pure achievement-evaluation rules shared by <see cref="Services.GamificationService"/>: given the
/// already-loaded user, earned-achievement set, and habit context, each check grants any newly
/// qualifying achievement into <c>newAchievements</c> (and updates the user's XP via
/// <see cref="TryGrant"/>). No I/O, no injected dependencies — every input is passed in.
/// </summary>
public static class AchievementChecks
{
    public const int PerfectStreakWindowDays = 30;

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
        user.AddXp(definition.XpReward);
        earned.Add(achievementId);
        newAchievements.Add((entity, definition));
    }

    public static void CheckConsistencyAchievements(
        int currentStreak,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        if (currentStreak >= 7)
            TryGrant(AchievementDefinitions.WeekWarrior, user, earned, newAchievements);
        if (currentStreak >= 14)
            TryGrant(AchievementDefinitions.FortnightFocus, user, earned, newAchievements);
        if (currentStreak >= 30)
            TryGrant(AchievementDefinitions.MonthlyMaster, user, earned, newAchievements);
        if (currentStreak >= 90)
            TryGrant(AchievementDefinitions.QuarterChampion, user, earned, newAchievements);
        if (currentStreak >= 100)
            TryGrant(AchievementDefinitions.Centurion, user, earned, newAchievements);
        if (currentStreak >= 180)
            TryGrant(AchievementDefinitions.HalfYearHero, user, earned, newAchievements);
        if (currentStreak >= 365)
            TryGrant(AchievementDefinitions.YearOfDiscipline, user, earned, newAchievements);
        if (currentStreak >= 500)
            TryGrant(AchievementDefinitions.StreakTitan, user, earned, newAchievements);
    }

    public static void CheckVolumeAchievements(
        int totalCompletions,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        if (totalCompletions >= 10)
            TryGrant(AchievementDefinitions.GettingMomentum, user, earned, newAchievements);
        if (totalCompletions >= 50)
            TryGrant(AchievementDefinitions.BuildingHabits, user, earned, newAchievements);
        if (totalCompletions >= 100)
            TryGrant(AchievementDefinitions.Dedicated, user, earned, newAchievements);
        if (totalCompletions >= 500)
            TryGrant(AchievementDefinitions.Relentless, user, earned, newAchievements);
        if (totalCompletions >= 1000)
            TryGrant(AchievementDefinitions.LegendaryVolume, user, earned, newAchievements);
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
            if (earlyCount >= 10)
                TryGrant(AchievementDefinitions.EarlyBird, user, earned, newAchievements);
        }

        if (checkNight)
        {
            var nightCount = logsWithRecentCreationTimes.Count(l =>
            {
                var userTime = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, userTz);
                return userTime.Hour >= 22;
            });
            if (nightCount >= 10)
                TryGrant(AchievementDefinitions.NightOwl, user, earned, newAchievements);
        }
    }
}
