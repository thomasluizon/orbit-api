using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Services;

/// <summary>
/// Groups repository dependencies for gamification to reduce constructor parameter count (S107).
/// </summary>
public record GamificationRepositories(
    IGenericRepository<User> UserRepository,
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<HabitLog> HabitLogRepository,
    IGenericRepository<Goal> GoalRepository,
    IGenericRepository<UserAchievement> AchievementRepository,
    IGenericRepository<Notification> NotificationRepository);

public partial class GamificationService(
    GamificationRepositories repos,
    IPushNotificationService pushService,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    ILogger<GamificationService> logger) : IGamificationService
{
    private const int AchievementLogWindowDays = 400;
    private const int PerfectStreakWindowDays = 30;
    private const int MaxConcurrencyAttempts = 3;

    private sealed record PendingPush(Guid UserId, string Title, string Body);

    private sealed record HabitsLoggedOutcome(IReadOnlyList<HabitLogGamificationResult> Results, bool ShouldSave);

    public async Task<HabitLogGamificationResult?> ProcessHabitLogged(Guid userId, Guid habitId, CancellationToken ct = default)
    {
        var results = await ProcessHabitsLogged(userId, [habitId], ct);
        return results.Count == 1 ? results[0] : null;
    }

    /// <summary>
    /// Evaluates XP, achievements, level-ups, and notifications for a set of just-logged habits
    /// in one pass, loading the shared user/achievement/habit context once for the whole batch.
    /// Returns one result per processed habit id, in input order; unknown ids are skipped.
    /// </summary>
    public async Task<IReadOnlyList<HabitLogGamificationResult>> ProcessHabitsLogged(
        Guid userId, IReadOnlyList<Guid> habitIds, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
                unitOfWork.ResetTracking();

            var pushes = new List<PendingPush>();
            var outcome = await ComputeHabitsLoggedAsync(userId, habitIds, pushes, ct);
            if (!outcome.ShouldSave)
                return outcome.Results;

            try
            {
                await unitOfWork.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                continue;
            }

            await FlushPushesAsync(pushes, ct);
            return outcome.Results;
        }
    }

    private async Task<HabitsLoggedOutcome> ComputeHabitsLoggedAsync(
        Guid userId, IReadOnlyList<Guid> habitIds, List<PendingPush> pushes, CancellationToken ct)
    {
        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null || !user.HasProAccess) return new HabitsLoggedOutcome([], ShouldSave: false);

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var today = await userDateService.GetUserTodayAsync(userId, ct);

        var streakLogCutoff = today.AddDays(-AchievementLogWindowDays);
        var loggedHabits = await repos.HabitRepository.FindAsync(
            h => h.UserId == userId && habitIds.Contains(h.Id),
            q => q.Include(h => h.Logs.Where(l => l.Date >= streakLogCutoff)),
            ct);
        if (loggedHabits.Count == 0) return new HabitsLoggedOutcome([], ShouldSave: false);

        var context = await LoadLoggedHabitsContext(user, earned, today, ct);

        var results = new List<HabitLogGamificationResult>();
        foreach (var habitId in habitIds)
        {
            var habit = loggedHabits.FirstOrDefault(h => h.Id == habitId);
            if (habit is null) continue;

            results.Add(await ProcessLoggedHabit(user, habit, earned, context, today, pushes, ct));
        }

        return new HabitsLoggedOutcome(results, ShouldSave: true);
    }

    private sealed record LoggedHabitsContext(
        IReadOnlyList<Habit> AllUserHabits,
        TimeZoneInfo UserTimeZone,
        int TotalLogCount,
        IReadOnlyList<HabitLog> LogsWithRecentCreationTimes,
        bool HasActivityInPriorWeek);

    private async Task<LoggedHabitsContext> LoadLoggedHabitsContext(
        User user, HashSet<string> earned, DateOnly today, CancellationToken ct)
    {
        var perfectStreakCutoff = today.AddDays(-PerfectStreakWindowDays);
        var allUserHabits = await repos.HabitRepository.FindAsync(
            h => h.UserId == user.Id,
            q => q.Include(h => h.Logs.Where(l => l.Date >= perfectStreakCutoff && l.Date <= today)),
            ct);
        var allHabitIds = allUserHabits.Select(h => h.Id).ToList();

        var totalLogCutoff = today.AddDays(-AchievementLogWindowDays);
        var totalLogCount = earned.Contains(AchievementDefinitions.Liftoff) && earned.Contains(AchievementDefinitions.LegendaryVolume)
            ? 0
            : await repos.HabitLogRepository.CountAsync(
                l => allHabitIds.Contains(l.HabitId) && l.Date >= totalLogCutoff, ct);

        var createdAtUtcCutoff = DateTime.UtcNow.AddDays(-90);
        IReadOnlyList<HabitLog> logsWithRecentCreationTimes =
            earned.Contains(AchievementDefinitions.EarlyBird) && earned.Contains(AchievementDefinitions.NightOwl)
                ? []
                : await repos.HabitLogRepository.FindAsync(
                    l => allHabitIds.Contains(l.HabitId) && l.CreatedAtUtc >= createdAtUtcCutoff, ct);

        var comebackCutoff = today.AddDays(-7);
        var hasActivityInPriorWeek = earned.Contains(AchievementDefinitions.Comeback)
            || await repos.HabitLogRepository.AnyAsync(
                l => allHabitIds.Contains(l.HabitId) && l.Date >= comebackCutoff && l.Date < today, ct);

        return new LoggedHabitsContext(
            allUserHabits,
            TimeZoneHelper.FindTimeZone(user.TimeZone),
            totalLogCount,
            logsWithRecentCreationTimes,
            hasActivityInPriorWeek);
    }

    private async Task<HabitLogGamificationResult> ProcessLoggedHabit(
        User user, Habit habit, HashSet<string> earned, LoggedHabitsContext context, DateOnly today,
        List<PendingPush> pushes, CancellationToken ct)
    {
        var previousLevel = user.Level;
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();

        var metrics = HabitMetricsCalculator.Calculate(habit, today, context.UserTimeZone);
        var xp = 10 + metrics.CurrentStreak;
        user.AddXp(xp);

        if (!earned.Contains(AchievementDefinitions.Liftoff) && context.TotalLogCount == 1)
            TryGrant(AchievementDefinitions.Liftoff, user, earned, newAchievements);

        CheckConsistencyAchievements(metrics.CurrentStreak, earned, user, newAchievements);

        if (!earned.Contains(AchievementDefinitions.LegendaryVolume))
            CheckVolumeAchievements(context.TotalLogCount, earned, user, newAchievements);

        CheckPerfectDay(context.AllUserHabits, today, earned, user, newAchievements);

        if (earned.Contains(AchievementDefinitions.PerfectDay)
            || newAchievements.Any(a => a.Definition.Id == AchievementDefinitions.PerfectDay))
        {
            CheckPerfectWeekAndMonth(context.AllUserHabits, today, earned, user, newAchievements);
        }

        CheckTimeBasedAchievements(user, earned, newAchievements, context.LogsWithRecentCreationTimes, context.UserTimeZone);

        if (!earned.Contains(AchievementDefinitions.Comeback) && !context.HasActivityInPriorWeek)
            TryGrant(AchievementDefinitions.Comeback, user, earned, newAchievements);

        if (!earned.Contains(AchievementDefinitions.BadHabitBreaker) && habit.IsBadHabit && metrics.CurrentStreak >= 30)
        {
            TryGrant(AchievementDefinitions.BadHabitBreaker, user, earned, newAchievements);
        }

        foreach (var (entity, _) in newAchievements)
            await repos.AchievementRepository.AddAsync(entity, ct);

        UpdateLevel(user);

        foreach (var (_, definition) in newAchievements)
            await QueueAchievementNotification(user.Id, definition, user.Language, pushes, ct);

        if (user.Level > previousLevel)
        {
            var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
            await QueueLevelUpNotification(user.Id, newLevel, user.Language, pushes, ct);
        }

        return new HabitLogGamificationResult(
            xp,
            newAchievements.Select(a => a.Definition.Id).ToList());
    }

    public async Task ProcessHabitCreated(Guid userId, CancellationToken ct = default)
    {
        await ProcessGamificationEventAsync(userId, async (user, earned, newAchievements) =>
        {
            if (!earned.Contains(AchievementDefinitions.FirstOrbit))
            {
                var habitCount = await repos.HabitRepository.CountAsync(h => h.UserId == userId && h.ParentHabitId == null, ct);
                if (habitCount == 1)
                    TryGrant(AchievementDefinitions.FirstOrbit, user, earned, newAchievements);
            }
        }, ct);
    }

    public async Task ProcessGoalCreated(Guid userId, CancellationToken ct = default)
    {
        await ProcessGamificationEventAsync(userId, async (user, earned, newAchievements) =>
        {
            var goalCount = await repos.GoalRepository.CountAsync(g => g.UserId == userId, ct);

            if (!earned.Contains(AchievementDefinitions.MissionControl) && goalCount == 1)
                TryGrant(AchievementDefinitions.MissionControl, user, earned, newAchievements);

            if (!earned.Contains(AchievementDefinitions.GoalSetter) && goalCount >= 3)
                TryGrant(AchievementDefinitions.GoalSetter, user, earned, newAchievements);
        }, ct);
    }

    public async Task ProcessGoalCompleted(Guid userId, CancellationToken ct = default)
    {
        await ProcessGamificationEventAsync(userId, async (user, earned, newAchievements) =>
        {
            user.AddXp(100);

            var completedGoals = await repos.GoalRepository.CountAsync(
                g => g.UserId == userId && g.Status == Domain.Enums.GoalStatus.Completed, ct);

            if (!earned.Contains(AchievementDefinitions.GoalCrusher) && completedGoals == 1)
                TryGrant(AchievementDefinitions.GoalCrusher, user, earned, newAchievements);

            if (!earned.Contains(AchievementDefinitions.Overachiever) && completedGoals >= 5)
                TryGrant(AchievementDefinitions.Overachiever, user, earned, newAchievements);

            if (!earned.Contains(AchievementDefinitions.DreamMaker) && completedGoals >= 10)
                TryGrant(AchievementDefinitions.DreamMaker, user, earned, newAchievements);
        }, ct);
    }

    /// <summary>
    /// Template method that handles the common gamification scaffold:
    /// load user, check Pro, load earned achievements, run domain-specific checks,
    /// persist achievements, update level, send notifications, save changes.
    /// </summary>
    private async Task ProcessGamificationEventAsync(
        Guid userId,
        Func<User, HashSet<string>, List<(UserAchievement Entity, AchievementDefinition Definition)>, Task> checkAchievements,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
                unitOfWork.ResetTracking();

            var pushes = new List<PendingPush>();
            var shouldSave = await ComputeGamificationEventAsync(userId, checkAchievements, pushes, ct);
            if (!shouldSave)
                return;

            try
            {
                await unitOfWork.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                continue;
            }

            await FlushPushesAsync(pushes, ct);
            return;
        }
    }

    private async Task<bool> ComputeGamificationEventAsync(
        Guid userId,
        Func<User, HashSet<string>, List<(UserAchievement Entity, AchievementDefinition Definition)>, Task> checkAchievements,
        List<PendingPush> pushes,
        CancellationToken ct)
    {
        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null || !user.HasProAccess) return false;

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        await checkAchievements(user, earned, newAchievements);

        foreach (var (entity, _) in newAchievements)
            await repos.AchievementRepository.AddAsync(entity, ct);

        UpdateLevel(user);

        foreach (var (_, definition) in newAchievements)
            await QueueAchievementNotification(userId, definition, user.Language, pushes, ct);

        if (user.Level > previousLevel)
        {
            var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
            await QueueLevelUpNotification(userId, newLevel, user.Language, pushes, ct);
        }

        return true;
    }

    private async Task<HashSet<string>> LoadEarnedAchievementIds(Guid userId, CancellationToken ct)
    {
        var earned = await repos.AchievementRepository.FindAsync(a => a.UserId == userId, ct);
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

    private static void CheckPerfectWeekAndMonth(
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

    private static void CheckTimeBasedAchievements(
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

    private static void UpdateLevel(User user)
    {
        var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
        if (newLevel.Level != user.Level)
            user.SetLevel(newLevel.Level);
    }

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

    private async Task QueueAchievementNotification(
        Guid userId, AchievementDefinition achievement, string? language, List<PendingPush> pushes, CancellationToken ct)
    {
        var isPt = LocaleHelper.IsPortuguese(language);
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
        await repos.NotificationRepository.AddAsync(notification, ct);

        pushes.Add(new PendingPush(userId, title, body));
    }

    private async Task QueueLevelUpNotification(
        Guid userId, LevelDefinition newLevel, string? language, List<PendingPush> pushes, CancellationToken ct)
    {
        var isPt = LocaleHelper.IsPortuguese(language);
        var title = isPt
            ? $"Subiu de nível! Agora você está no nível {newLevel.Level}"
            : $"Level Up! You're now Level {newLevel.Level}";
        var levelTitle = isPt && LevelTranslationsPt.TryGetValue(newLevel.Level, out var ptTitle)
            ? ptTitle : newLevel.Title;
        var body = isPt
            ? $"Você alcançou {levelTitle}! Continue assim!"
            : $"You've reached {newLevel.Title}! Keep going!";

        var notification = Notification.Create(userId, title, body);
        await repos.NotificationRepository.AddAsync(notification, ct);

        pushes.Add(new PendingPush(userId, title, body));
    }

    private async Task FlushPushesAsync(IReadOnlyList<PendingPush> pushes, CancellationToken ct)
    {
        foreach (var push in pushes)
        {
            try
            {
                await pushService.SendToUserAsync(push.UserId, push.Title, push.Body, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                LogPushNotificationFailed(logger, ex, push.UserId);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Push notification failed for user {UserId}")]
    private static partial void LogPushNotificationFailed(ILogger logger, Exception ex, Guid userId);
}
