using Microsoft.EntityFrameworkCore;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Services;

public class GamificationService(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<UserAchievement> achievementRepository,
    IGenericRepository<Notification> notificationRepository,
    IPushNotificationService pushService,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork) : IGamificationService
{
    public async Task ProcessHabitLogged(Guid userId, Guid habitId, CancellationToken ct = default)
    {
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null || !user.HasProAccess) return;

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        // Load the logged habit with logs for streak calculation
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId,
            q => q.Include(h => h.Logs),
            ct);

        if (habit is null) return;

        // Calculate XP: 10 base + streak bonus
        var metrics = HabitMetricsCalculator.Calculate(habit, today);
        var xp = 10 + metrics.CurrentStreak;
        user.AddXp(xp);

        // --- Liftoff (first completion) ---
        if (!earned.Contains(AchievementDefinitions.Liftoff))
        {
            var userHabits = await habitRepository.FindAsync(h => h.UserId == userId, q => q.Include(h => h.Logs), ct);
            var totalLogs = userHabits.Sum(h => h.Logs.Count);
            if (totalLogs == 1)
                TryGrant(AchievementDefinitions.Liftoff, user, earned, newAchievements);
        }

        // --- Consistency achievements ---
        CheckConsistencyAchievements(metrics.CurrentStreak, earned, user, newAchievements);

        // --- Volume achievements ---
        if (!earned.Contains(AchievementDefinitions.LegendaryVolume))
        {
            var userHabits = await habitRepository.FindAsync(h => h.UserId == userId, q => q.Include(h => h.Logs), ct);
            var totalCompletions = userHabits.Sum(h => h.Logs.Count);
            CheckVolumeAchievements(totalCompletions, earned, user, newAchievements);
        }

        // --- Perfect Day ---
        await CheckPerfectDay(userId, today, earned, user, newAchievements, ct);

        // --- Perfect Week / Perfect Month (only if PerfectDay is earned) ---
        if (earned.Contains(AchievementDefinitions.PerfectDay) || newAchievements.Any(a => a.Definition.Id == AchievementDefinitions.PerfectDay))
        {
            await CheckPerfectWeekAndMonth(userId, today, earned, user, newAchievements, ct);
        }

        // --- Early Bird / Night Owl ---
        await CheckTimeBasedAchievements(userId, user, earned, newAchievements, ct);

        // --- Comeback ---
        if (!earned.Contains(AchievementDefinitions.Comeback))
        {
            await CheckComeback(userId, today, earned, user, newAchievements, ct);
        }

        // --- Bad Habit Breaker ---
        if (!earned.Contains(AchievementDefinitions.BadHabitBreaker) && habit.IsBadHabit)
        {
            if (metrics.CurrentStreak >= 30)
                TryGrant(AchievementDefinitions.BadHabitBreaker, user, earned, newAchievements);
        }

        // Persist new achievements
        foreach (var (entity, _) in newAchievements)
            await achievementRepository.AddAsync(entity, ct);

        // Check level up
        UpdateLevel(user);

        // Notifications
        foreach (var (_, definition) in newAchievements)
            await SendAchievementNotification(userId, definition, user.Language, ct);

        if (user.Level > previousLevel)
        {
            var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
            await SendLevelUpNotification(userId, newLevel, user.Language, ct);
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task ProcessHabitCreated(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null || !user.HasProAccess) return;

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        // First Orbit: first habit created
        if (!earned.Contains(AchievementDefinitions.FirstOrbit))
        {
            var habitCount = (await habitRepository.FindAsync(h => h.UserId == userId && h.ParentHabitId == null, ct)).Count;
            if (habitCount == 1)
                TryGrant(AchievementDefinitions.FirstOrbit, user, earned, newAchievements);
        }

        foreach (var (entity, _) in newAchievements)
            await achievementRepository.AddAsync(entity, ct);

        UpdateLevel(user);

        foreach (var (_, definition) in newAchievements)
            await SendAchievementNotification(userId, definition, user.Language, ct);

        if (user.Level > previousLevel)
        {
            var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
            await SendLevelUpNotification(userId, newLevel, user.Language, ct);
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task ProcessGoalCreated(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null || !user.HasProAccess) return;

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        var goalCount = (await goalRepository.FindAsync(g => g.UserId == userId, ct)).Count;

        // Mission Control: first goal
        if (!earned.Contains(AchievementDefinitions.MissionControl) && goalCount == 1)
            TryGrant(AchievementDefinitions.MissionControl, user, earned, newAchievements);

        // Goal Setter: 3 goals
        if (!earned.Contains(AchievementDefinitions.GoalSetter) && goalCount >= 3)
            TryGrant(AchievementDefinitions.GoalSetter, user, earned, newAchievements);

        foreach (var (entity, _) in newAchievements)
            await achievementRepository.AddAsync(entity, ct);

        UpdateLevel(user);

        foreach (var (_, definition) in newAchievements)
            await SendAchievementNotification(userId, definition, user.Language, ct);

        if (user.Level > previousLevel)
        {
            var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
            await SendLevelUpNotification(userId, newLevel, user.Language, ct);
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task ProcessGoalCompleted(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null || !user.HasProAccess) return;

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        // 100 XP for goal completion
        user.AddXp(100);

        var completedGoals = (await goalRepository.FindAsync(
            g => g.UserId == userId && g.Status == Domain.Enums.GoalStatus.Completed, ct)).Count;

        // Goal Crusher: first completed goal
        if (!earned.Contains(AchievementDefinitions.GoalCrusher) && completedGoals == 1)
            TryGrant(AchievementDefinitions.GoalCrusher, user, earned, newAchievements);

        // Overachiever: 5 completed goals
        if (!earned.Contains(AchievementDefinitions.Overachiever) && completedGoals >= 5)
            TryGrant(AchievementDefinitions.Overachiever, user, earned, newAchievements);

        // Dream Maker: 10 completed goals
        if (!earned.Contains(AchievementDefinitions.DreamMaker) && completedGoals >= 10)
            TryGrant(AchievementDefinitions.DreamMaker, user, earned, newAchievements);

        foreach (var (entity, _) in newAchievements)
            await achievementRepository.AddAsync(entity, ct);

        UpdateLevel(user);

        foreach (var (_, definition) in newAchievements)
            await SendAchievementNotification(userId, definition, user.Language, ct);

        if (user.Level > previousLevel)
        {
            var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
            await SendLevelUpNotification(userId, newLevel, user.Language, ct);
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    // --- Private helpers ---

    private async Task<HashSet<string>> LoadEarnedAchievementIds(Guid userId, CancellationToken ct)
    {
        var earned = await achievementRepository.FindAsync(a => a.UserId == userId, ct);
        return earned.Select(a => a.AchievementId).ToHashSet();
    }

    private static void TryGrant(
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

    private static void CheckConsistencyAchievements(
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
        if (currentStreak >= 365)
            TryGrant(AchievementDefinitions.YearOfDiscipline, user, earned, newAchievements);
    }

    private static void CheckVolumeAchievements(
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

    private async Task CheckPerfectDay(
        Guid userId,
        DateOnly today,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements,
        CancellationToken ct)
    {
        if (earned.Contains(AchievementDefinitions.PerfectDay)) return;

        // Load all active, non-general, top-level habits for this user
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == userId && !h.IsCompleted && !h.IsGeneral && h.ParentHabitId == null,
            q => q.Include(h => h.Logs),
            ct);

        if (allHabits.Count == 0) return;

        // Filter to habits scheduled for today
        var scheduledToday = allHabits.Where(h => IsScheduledForDate(h, today)).ToList();
        if (scheduledToday.Count == 0) return;

        // Check if all scheduled habits have a log for today
        var allDone = scheduledToday.All(h => h.Logs.Any(l => l.Date == today));
        if (allDone)
            TryGrant(AchievementDefinitions.PerfectDay, user, earned, newAchievements);
    }

    private async Task CheckPerfectWeekAndMonth(
        Guid userId,
        DateOnly today,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements,
        CancellationToken ct)
    {
        // Load all active, non-general, top-level habits with logs
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == userId && !h.IsCompleted && !h.IsGeneral && h.ParentHabitId == null,
            q => q.Include(h => h.Logs),
            ct);

        if (allHabits.Count == 0) return;

        // Check consecutive perfect days going back from today
        var consecutivePerfectDays = 0;
        for (var day = today; day >= today.AddDays(-30); day = day.AddDays(-1))
        {
            var scheduledForDay = allHabits.Where(h => IsScheduledForDate(h, day)).ToList();
            if (scheduledForDay.Count == 0)
            {
                // No habits scheduled this day, skip but don't break streak
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

    private async Task CheckTimeBasedAchievements(
        Guid userId,
        User user,
        HashSet<string> earned,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements,
        CancellationToken ct)
    {
        var checkEarly = !earned.Contains(AchievementDefinitions.EarlyBird);
        var checkNight = !earned.Contains(AchievementDefinitions.NightOwl);
        if (!checkEarly && !checkNight) return;

        var userTz = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone ?? "UTC");

        // Load all habit IDs for this user
        var userHabits = await habitRepository.FindAsync(h => h.UserId == userId, ct);
        var habitIds = userHabits.Select(h => h.Id).ToList();
        if (habitIds.Count == 0) return;

        // Load all logs for user's habits
        var allLogs = await habitLogRepository.FindAsync(l => habitIds.Contains(l.HabitId), ct);

        if (checkEarly)
        {
            var earlyCount = allLogs.Count(l =>
            {
                var userTime = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, userTz);
                return userTime.Hour < 7;
            });
            if (earlyCount >= 10)
                TryGrant(AchievementDefinitions.EarlyBird, user, earned, newAchievements);
        }

        if (checkNight)
        {
            var nightCount = allLogs.Count(l =>
            {
                var userTime = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, userTz);
                return userTime.Hour >= 22;
            });
            if (nightCount >= 10)
                TryGrant(AchievementDefinitions.NightOwl, user, earned, newAchievements);
        }
    }

    private async Task CheckComeback(
        Guid userId,
        DateOnly today,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements,
        CancellationToken ct)
    {
        if (earned.Contains(AchievementDefinitions.Comeback)) return;

        var sevenDaysAgo = today.AddDays(-7);

        // Load all habit IDs for this user
        var userHabits = await habitRepository.FindAsync(h => h.UserId == userId, ct);
        var habitIds = userHabits.Select(h => h.Id).ToList();
        if (habitIds.Count == 0) return;

        // Check for any logs in the 7 days before today (not including today)
        var recentLogs = await habitLogRepository.FindAsync(
            l => habitIds.Contains(l.HabitId) && l.Date >= sevenDaysAgo && l.Date < today, ct);

        if (recentLogs.Count == 0)
            TryGrant(AchievementDefinitions.Comeback, user, earned, newAchievements);
    }

    private static void UpdateLevel(User user)
    {
        var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
        if (newLevel.Level != user.Level)
            user.SetLevel(newLevel.Level);
    }

    private async Task SendAchievementNotification(Guid userId, AchievementDefinition achievement, string? language, CancellationToken ct)
    {
        var isPt = language?.StartsWith("pt") == true;
        var title = isPt
            ? $"Conquista Desbloqueada: {achievement.Name}"
            : $"Achievement Unlocked: {achievement.Name}";
        var body = $"{achievement.Description} (+{achievement.XpReward} XP)";

        var notification = Notification.Create(userId, title, body);
        await notificationRepository.AddAsync(notification, ct);

        try
        {
            await pushService.SendToUserAsync(userId, title, body, cancellationToken: ct);
        }
        catch
        {
            // Push failure should not block gamification
        }
    }

    private async Task SendLevelUpNotification(Guid userId, LevelDefinition newLevel, string? language, CancellationToken ct)
    {
        var isPt = language?.StartsWith("pt") == true;
        var title = isPt
            ? $"Subiu de nivel! Agora voce e Nivel {newLevel.Level}"
            : $"Level Up! You're now Level {newLevel.Level}";
        var body = isPt
            ? $"Voce alcancou {newLevel.Title}! Continue assim!"
            : $"You've reached {newLevel.Title}! Keep going!";

        var notification = Notification.Create(userId, title, body);
        await notificationRepository.AddAsync(notification, ct);

        try
        {
            await pushService.SendToUserAsync(userId, title, body, cancellationToken: ct);
        }
        catch
        {
            // Push failure should not block gamification
        }
    }

    private static bool IsScheduledForDate(Habit habit, DateOnly date)
    {
        // One-time task: scheduled for its due date
        if (habit.FrequencyUnit is null || habit.FrequencyQuantity is null)
            return habit.DueDate == date;

        // General habits are never "scheduled" for a specific date
        if (habit.IsGeneral) return false;

        // Habit hasn't started yet
        if (date < habit.DueDate) return false;

        // Habit has ended
        if (habit.EndDate.HasValue && date > habit.EndDate.Value) return false;

        // Day-specific: check if the day of week matches
        if (habit.Days.Count > 0 && habit.FrequencyQuantity == 1)
            return habit.Days.Contains(date.DayOfWeek);

        // Daily: every N days
        if (habit.FrequencyUnit == Domain.Enums.FrequencyUnit.Day)
        {
            var daysSinceStart = date.DayNumber - habit.DueDate.DayNumber;
            return daysSinceStart % habit.FrequencyQuantity.Value == 0;
        }

        // Weekly/Monthly/Yearly: simplified check (due date alignment)
        return true;
    }
}
