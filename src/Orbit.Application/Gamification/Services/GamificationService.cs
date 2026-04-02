using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
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
    IUnitOfWork unitOfWork,
    ILogger<GamificationService> logger) : IGamificationService
{
    public async Task<HabitLogGamificationResult?> ProcessHabitLogged(Guid userId, Guid habitId, CancellationToken ct = default)
    {
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null || !user.HasProAccess) return null;

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        // --- Load all user habits with logs ONCE (reused across streak, volume, perfect-day, perfect-week checks) ---
        var allUserHabits = await habitRepository.FindAsync(
            h => h.UserId == userId,
            q => q.Include(h => h.Logs),
            ct);

        // Find the specific logged habit from the already-loaded collection
        var habit = allUserHabits.FirstOrDefault(h => h.Id == habitId);
        if (habit is null) return null;

        // Calculate XP: 10 base + streak bonus
        var userTz = TimeZoneHelper.FindTimeZone(user.TimeZone);
        var metrics = HabitMetricsCalculator.Calculate(habit, today, userTz);
        var xp = 10 + metrics.CurrentStreak;
        user.AddXp(xp);

        // Update global user streak (idempotent per day via LastActiveDate guard)
        user.UpdateStreak(today);

        // --- Liftoff (first completion) ---
        if (!earned.Contains(AchievementDefinitions.Liftoff))
        {
            var totalLogs = allUserHabits.Sum(h => h.Logs.Count);
            if (totalLogs == 1)
                TryGrant(AchievementDefinitions.Liftoff, user, earned, newAchievements);
        }

        // --- Consistency achievements ---
        CheckConsistencyAchievements(metrics.CurrentStreak, earned, user, newAchievements);

        // --- Volume achievements ---
        if (!earned.Contains(AchievementDefinitions.LegendaryVolume))
        {
            var totalCompletions = allUserHabits.Sum(h => h.Logs.Count);
            CheckVolumeAchievements(totalCompletions, earned, user, newAchievements);
        }

        // --- Perfect Day ---
        CheckPerfectDay(allUserHabits, today, earned, user, newAchievements);

        // --- Perfect Week / Perfect Month (only if PerfectDay is earned) ---
        if (earned.Contains(AchievementDefinitions.PerfectDay) || newAchievements.Any(a => a.Definition.Id == AchievementDefinitions.PerfectDay))
        {
            CheckPerfectWeekAndMonth(allUserHabits, today, earned, user, newAchievements);
        }

        // --- Early Bird / Night Owl ---
        await CheckTimeBasedAchievements(userId, user, earned, newAchievements, allUserHabits, ct);

        // --- Comeback ---
        if (!earned.Contains(AchievementDefinitions.Comeback))
        {
            await CheckComeback(userId, today, earned, user, newAchievements, allUserHabits, ct);
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

        return new HabitLogGamificationResult(
            xp,
            newAchievements.Select(a => a.Definition.Id).ToList());
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
            var habitCount = await habitRepository.CountAsync(h => h.UserId == userId && h.ParentHabitId == null, ct);
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

        var goalCount = await goalRepository.CountAsync(g => g.UserId == userId, ct);

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

        var completedGoals = await goalRepository.CountAsync(
            g => g.UserId == userId && g.Status == Domain.Enums.GoalStatus.Completed, ct);

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

    private static void CheckPerfectDay(
        IReadOnlyList<Habit> allUserHabits,
        DateOnly today,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        if (earned.Contains(AchievementDefinitions.PerfectDay)) return;

        // Filter to active, non-general, top-level habits
        var eligibleHabits = allUserHabits
            .Where(h => !h.IsCompleted && !h.IsGeneral && h.ParentHabitId == null)
            .ToList();

        if (eligibleHabits.Count == 0) return;

        // Filter to habits scheduled for today
        var scheduledToday = eligibleHabits.Where(h => HabitScheduleService.IsHabitDueOnDate(h, today)).ToList();
        if (scheduledToday.Count == 0) return;

        // Check if all scheduled habits have a log for today
        var allDone = scheduledToday.All(h => h.Logs.Any(l => l.Date == today));
        if (allDone)
            TryGrant(AchievementDefinitions.PerfectDay, user, earned, newAchievements);
    }

    private static void CheckPerfectWeekAndMonth(
        IReadOnlyList<Habit> allUserHabits,
        DateOnly today,
        HashSet<string> earned,
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements)
    {
        // Filter to active, non-general, top-level habits
        var eligibleHabits = allUserHabits
            .Where(h => !h.IsCompleted && !h.IsGeneral && h.ParentHabitId == null)
            .ToList();

        if (eligibleHabits.Count == 0) return;

        // Check consecutive perfect days going back from today
        var consecutivePerfectDays = 0;
        for (var day = today; day >= today.AddDays(-30); day = day.AddDays(-1))
        {
            var scheduledForDay = eligibleHabits.Where(h => HabitScheduleService.IsHabitDueOnDate(h, day)).ToList();
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
        IReadOnlyList<Habit> allUserHabits,
        CancellationToken ct)
    {
        var checkEarly = !earned.Contains(AchievementDefinitions.EarlyBird);
        var checkNight = !earned.Contains(AchievementDefinitions.NightOwl);
        if (!checkEarly && !checkNight) return;

        var userTz = TimeZoneHelper.FindTimeZone(user.TimeZone);

        // Use pre-loaded habits instead of re-querying
        var habitIds = allUserHabits.Select(h => h.Id).ToList();
        if (habitIds.Count == 0) return;

        // Load recent logs only -- 90 days is more than enough to detect 10 qualifying entries
        var cutoff = DateTime.UtcNow.AddDays(-90);
        var allLogs = await habitLogRepository.FindAsync(
            l => habitIds.Contains(l.HabitId) && l.CreatedAtUtc >= cutoff, ct);

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
        IReadOnlyList<Habit> allUserHabits,
        CancellationToken ct)
    {
        if (earned.Contains(AchievementDefinitions.Comeback)) return;

        var sevenDaysAgo = today.AddDays(-7);

        // Use pre-loaded habits instead of re-querying
        var habitIds = allUserHabits.Select(h => h.Id).ToList();
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

    // TODO (Issue 52): These hardcoded Portuguese translations should be moved to a proper
    // localization resource file (e.g., .resx or a JSON-backed IStringLocalizer) once a
    // backend i18n strategy is established. Currently only pt-BR is supported; adding more
    // languages would require duplicating this pattern, which is not maintainable.
    private static readonly Dictionary<string, (string Name, string Description)> AchievementTranslationsPt = new()
    {
        ["first_orbit"] = ("Primeira Órbita", "Crie seu primeiro hábito"),
        ["liftoff"] = ("Decolagem", "Complete seu primeiro hábito"),
        ["mission_control"] = ("Controle de Missão", "Crie sua primeira meta"),
        ["week_warrior"] = ("Guerreiro da Semana", "Alcance uma sequência de 7 dias"),
        ["fortnight_focus"] = ("Foco Quinzenal", "Alcance uma sequência de 14 dias"),
        ["monthly_master"] = ("Mestre Mensal", "Alcance uma sequência de 30 dias"),
        ["quarter_champion"] = ("Campeão Trimestral", "Alcance uma sequência de 90 dias"),
        ["centurion"] = ("Centurião", "Alcance uma sequência de 100 dias"),
        ["year_of_discipline"] = ("Ano de Disciplina", "Alcance uma sequência de 365 dias"),
        ["getting_momentum"] = ("Ganhando Ritmo", "Complete 10 hábitos no total"),
        ["building_habits"] = ("Construindo Hábitos", "Complete 50 hábitos no total"),
        ["dedicated"] = ("Dedicado", "Complete 100 hábitos no total"),
        ["relentless"] = ("Imparável", "Complete 500 hábitos no total"),
        ["legendary"] = ("Lendário", "Complete 1.000 hábitos no total"),
        ["goal_setter"] = ("Definidor de Metas", "Crie 3 metas"),
        ["goal_crusher"] = ("Destruidor de Metas", "Complete sua primeira meta"),
        ["overachiever"] = ("Acima das Expectativas", "Complete 5 metas"),
        ["dream_maker"] = ("Realizador de Sonhos", "Complete 10 metas"),
        ["perfect_day"] = ("Dia Perfeito", "Complete todos os hábitos em um dia"),
        ["perfect_week"] = ("Semana Perfeita", "Complete todos os hábitos por 7 dias consecutivos"),
        ["perfect_month"] = ("Mês Perfeito", "Complete todos os hábitos por 30 dias consecutivos"),
        ["early_bird"] = ("Madrugador", "Complete um hábito antes das 7h (10 vezes)"),
        ["night_owl"] = ("Coruja Noturna", "Complete um hábito após as 22h (10 vezes)"),
        ["comeback"] = ("Retorno", "Retome após 7+ dias de inatividade"),
        ["bad_habit_breaker"] = ("Quebrador de Maus Hábitos", "Resista a um mau hábito por 30 dias consecutivos"),
    };

    private static readonly Dictionary<int, string> LevelTranslationsPt = new()
    {
        [1] = "Iniciante",
        [2] = "Explorador",
        [3] = "Orbitador",
        [4] = "Navegador",
        [5] = "Piloto",
        [6] = "Capitão",
        [7] = "Comandante",
        [8] = "Almirante",
        [9] = "Elite",
        [10] = "Lenda"
    };

    private async Task SendAchievementNotification(Guid userId, AchievementDefinition achievement, string? language, CancellationToken ct)
    {
        var isPt = language?.StartsWith("pt") == true;
        string name, description;

        if (isPt && AchievementTranslationsPt.TryGetValue(achievement.Id, out var pt))
        {
            name = pt.Name;
            description = pt.Description;
        }
        else
        {
            name = achievement.Name;
            description = achievement.Description;
        }

        var title = isPt
            ? $"Conquista Desbloqueada: {name}"
            : $"Achievement Unlocked: {name}";
        var body = $"{description} (+{achievement.XpReward} XP)";

        var notification = Notification.Create(userId, title, body);
        await notificationRepository.AddAsync(notification, ct);

        try
        {
            await pushService.SendToUserAsync(userId, title, body, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Push notification failed for achievement {AchievementId} for user {UserId}", achievement.Id, userId);
        }
    }

    private async Task SendLevelUpNotification(Guid userId, LevelDefinition newLevel, string? language, CancellationToken ct)
    {
        var isPt = language?.StartsWith("pt") == true;
        var title = isPt
            ? $"Subiu de nível! Agora você está no nível {newLevel.Level}"
            : $"Level Up! You're now Level {newLevel.Level}";
        var levelTitle = isPt && LevelTranslationsPt.TryGetValue(newLevel.Level, out var ptTitle)
            ? ptTitle : newLevel.Title;
        var body = isPt
            ? $"Você alcançou {levelTitle}! Continue assim!"
            : $"You've reached {newLevel.Title}! Keep going!";

        var notification = Notification.Create(userId, title, body);
        await notificationRepository.AddAsync(notification, ct);

        try
        {
            await pushService.SendToUserAsync(userId, title, body, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Push notification failed for level up to level {Level} for user {UserId}", newLevel.Level, userId);
        }
    }

}
