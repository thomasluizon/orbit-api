using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Habits.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
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

/// <summary>Groups the outbound notification channels gamification emits through to keep the service constructor small.</summary>
public record GamificationNotifiers(
    IPushNotificationService PushService,
    IFriendFeedEventEmitter FriendFeedEventEmitter);

public partial class GamificationService(
    GamificationRepositories repos,
    GamificationNotifiers notifiers,
    IUserDateService userDateService,
    IXpAwarder xpAwarder,
    IUnitOfWork unitOfWork,
    IFeatureFlagService featureFlagService,
    ILogger<GamificationService> logger) : IGamificationService
{
    // Streak window covers the 1000-day StreakImmortal target; volume window covers the 2500-completion Unstoppable target. Both must exceed their largest achievement target or those achievements can never be granted. https://github.com/thomasluizon/orbit-api/pull/419
    private const int StreakLogWindowDays = 1100;
    private const int TotalCompletionWindowDays = 2750;
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
        var attempt = 1;
        while (true)
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
                attempt++;
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
        if (user is null || !await IsGamificationUnlockedAsync(user, ct))
            return new HabitsLoggedOutcome([], ShouldSave: false);

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var today = await userDateService.GetUserTodayAsync(userId, ct);

        var streakLogCutoff = today.AddDays(-StreakLogWindowDays);
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
        var perfectStreakCutoff = today.AddDays(-AchievementChecks.PerfectStreakWindowDays);
        var allUserHabits = await repos.HabitRepository.FindAsync(
            h => h.UserId == user.Id,
            q => q.Include(h => h.Logs.Where(l => l.Date >= perfectStreakCutoff && l.Date <= today)),
            ct);
        var allHabitIds = allUserHabits.Select(h => h.Id).ToList();

        var totalLogCutoff = today.AddDays(-TotalCompletionWindowDays);
        var totalLogCount = earned.Contains(AchievementDefinitions.Liftoff) && earned.Contains(AchievementDefinitions.Unstoppable)
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

        var xpEarned = habit.IsBadHabit
            ? 0
            : await AwardLoggedHabitXpAndAchievementsAsync(
                user, habit, new AchievementAccumulator(earned, newAchievements), context, today, metrics.CurrentStreak, ct);

        if (habit.IsBadHabit
            && user.HasProAccess
            && !earned.Contains(AchievementDefinitions.BadHabitBreaker)
            && metrics.CurrentStreak >= 30)
        {
            AchievementChecks.TryGrant(AchievementDefinitions.BadHabitBreaker, user, earned, newAchievements);
        }

        await PersistNewAchievementsAsync(user, newAchievements, ct);

        LevelDefinitions.SyncLevel(user);

        foreach (var (_, definition) in newAchievements)
            await QueueAchievementNotification(user.Id, definition, user.Language, pushes, ct);

        if (user.Level > previousLevel)
        {
            var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
            await QueueLevelUpNotification(user.Id, newLevel, user.Language, pushes, ct);
        }

        return new HabitLogGamificationResult(
            xpEarned,
            newAchievements.Select(a => a.Definition.Id).ToList());
    }

    private sealed record AchievementAccumulator(
        HashSet<string> Earned,
        List<(UserAchievement Entity, AchievementDefinition Definition)> New);

    /// <summary>
    /// Grants the base + streak XP and runs every generic completion achievement check for a good
    /// habit log. Bad-habit logs never reach this — logging a habit you are trying to quit is a slip,
    /// not progress, so it earns no XP and no generic achievements (only <c>BadHabitBreaker</c>,
    /// evaluated separately, rewards a sustained abstinence streak). XP is awarded whenever gamification
    /// is unlocked (free tier included); the achievement checks run only for Pro users — free users earn
    /// XP and level up, but the achievement catalog stays Pro-gated. Returns the XP awarded.
    /// </summary>
    private async Task<int> AwardLoggedHabitXpAndAchievementsAsync(
        User user, Habit habit, AchievementAccumulator accumulator, LoggedHabitsContext context, DateOnly today,
        int currentStreak, CancellationToken ct)
    {
        var (earned, newAchievements) = accumulator;
        var xp = 10 + currentStreak;
        var habitLogId = habit.Logs
            .Where(l => l.Date == today && l.Value > 0)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefault();
        await AwardXpAsync(user, xp, XpAwardSource.HabitLog, habitLogId, awardedAtUtc: DateTime.UtcNow, ct);

        if (!user.HasProAccess)
            return xp;

        if (!earned.Contains(AchievementDefinitions.Liftoff) && context.TotalLogCount == 1)
            AchievementChecks.TryGrant(AchievementDefinitions.Liftoff, user, earned, newAchievements);

        AchievementChecks.CheckConsistencyAchievements(currentStreak, earned, user, newAchievements);

        if (!earned.Contains(AchievementDefinitions.Unstoppable))
            AchievementChecks.CheckVolumeAchievements(context.TotalLogCount, earned, user, newAchievements);

        AchievementChecks.CheckPerfectDay(context.AllUserHabits, today, earned, user, newAchievements);

        if (earned.Contains(AchievementDefinitions.PerfectDay)
            || newAchievements.Any(a => a.Definition.Id == AchievementDefinitions.PerfectDay))
        {
            AchievementChecks.CheckPerfectWeekAndMonth(context.AllUserHabits, today, earned, user, newAchievements);
        }

        AchievementChecks.CheckTimeBasedAchievements(user, earned, newAchievements, context.LogsWithRecentCreationTimes, context.UserTimeZone);

        if (!earned.Contains(AchievementDefinitions.Comeback) && !context.HasActivityInPriorWeek)
            AchievementChecks.TryGrant(AchievementDefinitions.Comeback, user, earned, newAchievements);

        return xp;
    }

    public async Task ProcessHabitCreated(Guid userId, CancellationToken ct = default)
    {
        await ProcessGamificationEventAsync(userId, async (user, earned, newAchievements) =>
        {
            if (!user.HasProAccess) return;

            if (!earned.Contains(AchievementDefinitions.FirstOrbit))
            {
                var habitCount = await repos.HabitRepository.CountAsync(h => h.UserId == userId && h.ParentHabitId == null, ct);
                if (habitCount == 1)
                    AchievementChecks.TryGrant(AchievementDefinitions.FirstOrbit, user, earned, newAchievements);
            }
        }, ct);
    }

    public async Task ProcessGoalCreated(Guid userId, CancellationToken ct = default)
    {
        await ProcessGamificationEventAsync(userId, async (user, earned, newAchievements) =>
        {
            if (!user.HasProAccess) return;

            var goalCount = await repos.GoalRepository.CountAsync(g => g.UserId == userId, ct);

            if (!earned.Contains(AchievementDefinitions.MissionControl) && goalCount == 1)
                AchievementChecks.TryGrant(AchievementDefinitions.MissionControl, user, earned, newAchievements);

            if (!earned.Contains(AchievementDefinitions.GoalSetter) && goalCount >= 3)
                AchievementChecks.TryGrant(AchievementDefinitions.GoalSetter, user, earned, newAchievements);
        }, ct);
    }

    public async Task ProcessGoalCompleted(Guid userId, CancellationToken ct = default)
    {
        await ProcessGamificationEventAsync(userId, async (user, earned, newAchievements) =>
        {
            await AwardXpAsync(user, 100, XpAwardSource.GoalCompleted, sourceId: null, awardedAtUtc: DateTime.UtcNow, ct);

            if (!user.HasProAccess) return;

            var completedGoals = await repos.GoalRepository.CountAsync(
                g => g.UserId == userId && g.Status == Domain.Enums.GoalStatus.Completed, ct);

            if (!earned.Contains(AchievementDefinitions.GoalCrusher) && completedGoals == 1)
                AchievementChecks.TryGrant(AchievementDefinitions.GoalCrusher, user, earned, newAchievements);

            if (!earned.Contains(AchievementDefinitions.Overachiever) && completedGoals >= 5)
                AchievementChecks.TryGrant(AchievementDefinitions.Overachiever, user, earned, newAchievements);

            if (!earned.Contains(AchievementDefinitions.DreamMaker) && completedGoals >= 10)
                AchievementChecks.TryGrant(AchievementDefinitions.DreamMaker, user, earned, newAchievements);
        }, ct);
    }

    /// <summary>
    /// Advances the onboarding setup-checklist flags from a single signal and, once all three
    /// (habit created, habit logged, Astra used) are set, marks the checklist complete. The signal
    /// and completion flags apply to every user un-gated so the client card hides consistently;
    /// the <see cref="AchievementDefinitions.OnboardingComplete"/> achievement is granted only to
    /// users with Pro access (#186). Short-circuits once the checklist is already complete.
    /// </summary>
    public async Task ProcessOnboardingChecklistAsync(
        Guid userId, OnboardingChecklistSignal signal, CancellationToken ct = default)
    {
        var attempt = 1;
        while (true)
        {
            if (attempt > 1)
                unitOfWork.ResetTracking();

            var pushes = new List<PendingPush>();
            var shouldSave = await ComputeOnboardingChecklistAsync(userId, signal, pushes, ct);
            if (!shouldSave)
                return;

            try
            {
                await unitOfWork.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                attempt++;
                continue;
            }

            await FlushPushesAsync(pushes, ct);
            return;
        }
    }

    private async Task<bool> ComputeOnboardingChecklistAsync(
        Guid userId, OnboardingChecklistSignal signal, List<PendingPush> pushes, CancellationToken ct)
    {
        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null || user.HasCompletedOnboardingChecklist)
            return false;

        ApplyOnboardingSignal(user, signal);

        if (!(user.HasCreatedFirstHabit && user.HasLoggedFirstHabit && user.HasTriedAstra))
            return true;

        user.CompleteOnboardingChecklist();

        if (!user.HasProAccess)
            return true;

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        AchievementChecks.CheckOnboardingChecklist(user, earned, newAchievements);

        await PersistNewAchievementsAsync(user, newAchievements, ct);

        LevelDefinitions.SyncLevel(user);

        foreach (var (_, definition) in newAchievements)
            await QueueAchievementNotification(userId, definition, user.Language, pushes, ct);

        if (user.Level > previousLevel)
        {
            var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
            await QueueLevelUpNotification(userId, newLevel, user.Language, pushes, ct);
        }

        return true;
    }

    private static void ApplyOnboardingSignal(User user, OnboardingChecklistSignal signal)
    {
        switch (signal)
        {
            case OnboardingChecklistSignal.HabitCreated:
                user.MarkFirstHabitCreated();
                break;
            case OnboardingChecklistSignal.HabitLogged:
                user.MarkFirstHabitLogged();
                break;
            case OnboardingChecklistSignal.AstraUsed:
                user.MarkAstraUsed();
                break;
        }
    }

    public async Task<IReadOnlyList<string>> TryGrantAchievementsAsync(
        Guid userId, IReadOnlyList<string> achievementIds, CancellationToken ct = default)
    {
        var attempt = 1;
        while (true)
        {
            if (attempt > 1)
                unitOfWork.ResetTracking();

            var pushes = new List<PendingPush>();
            var granted = await ComputeGrantAchievementsAsync(userId, achievementIds, pushes, ct);
            if (granted.Count == 0)
                return granted;

            try
            {
                await unitOfWork.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                attempt++;
                continue;
            }

            await FlushPushesAsync(pushes, ct);
            return granted;
        }
    }

    /// <summary>
    /// Idempotently grants the requested achievements to the loaded user, reusing the audited
    /// persist + XP + level + notification funnel. Returns the ids newly granted this call (empty when the
    /// user is missing or every id was already earned). Deliberately ungated on Pro — event-driven
    /// social/sharing achievements are earned by free users, while the catalog display stays Pro-gated.
    /// </summary>
    private async Task<IReadOnlyList<string>> ComputeGrantAchievementsAsync(
        Guid userId, IReadOnlyList<string> achievementIds, List<PendingPush> pushes, CancellationToken ct)
    {
        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null) return [];

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        foreach (var achievementId in achievementIds)
            AchievementChecks.TryGrant(achievementId, user, earned, newAchievements);

        if (newAchievements.Count == 0) return [];

        await PersistNewAchievementsAsync(user, newAchievements, ct);

        LevelDefinitions.SyncLevel(user);

        foreach (var (_, definition) in newAchievements)
            await QueueAchievementNotification(userId, definition, user.Language, pushes, ct);

        if (user.Level > previousLevel)
        {
            var newLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
            await QueueLevelUpNotification(userId, newLevel, user.Language, pushes, ct);
        }

        return newAchievements.Select(a => a.Definition.Id).ToList();
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
        var attempt = 1;
        while (true)
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
                attempt++;
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
        if (user is null || !await IsGamificationUnlockedAsync(user, ct)) return false;

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        await checkAchievements(user, earned, newAchievements);

        await PersistNewAchievementsAsync(user, newAchievements, ct);

        LevelDefinitions.SyncLevel(user);

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

    /// <summary>
    /// Whether gamification earning (streak, XP, levels) is active for the user: always for Pro, and for
    /// free users once the <see cref="FeatureFlagKeys.GamificationFreeTier"/> flag is enabled. Achievement
    /// awards remain Pro-gated independently of this predicate.
    /// </summary>
    private async Task<bool> IsGamificationUnlockedAsync(User user, CancellationToken ct)
    {
        if (user.HasProAccess) return true;
        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(user.Id, ct);
        return enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier);
    }

    private Task AwardXpAsync(
        User user, int amount, XpAwardSource source, Guid? sourceId, DateTime awardedAtUtc, CancellationToken ct)
        => xpAwarder.AwardAsync(user, amount, source, sourceId, awardedAtUtc, ct);

    private async Task PersistNewAchievementsAsync(
        User user,
        List<(UserAchievement Entity, AchievementDefinition Definition)> newAchievements,
        CancellationToken ct)
    {
        foreach (var (entity, definition) in newAchievements)
        {
            await repos.AchievementRepository.AddAsync(entity, ct);
            await AwardXpAsync(user, definition.XpReward, XpAwardSource.Achievement, entity.Id, awardedAtUtc: DateTime.UtcNow, ct);
            await EmitAchievementFeedEventAsync(user, entity, definition, ct);
        }
    }

    /// <summary>
    /// Streams a non-streak achievement into friends' feeds. Consistency (streak-tier) achievements are
    /// skipped because the streak hook already emits a StreakMilestone for the same moment, so emitting
    /// here too would double the feed row.
    /// </summary>
    private async Task EmitAchievementFeedEventAsync(
        User user, UserAchievement entity, AchievementDefinition definition, CancellationToken ct)
    {
        if (definition.Category == AchievementCategory.Consistency)
            return;

        await notifiers.FriendFeedEventEmitter.EmitAchievementEventAsync(user, entity.AchievementId, definition.Category, ct);
    }

    private static readonly Dictionary<string, (string Name, string Description)> AchievementTranslationsPt = new()
    {
        ["first_orbit"] = ("Primeira Órbita", "Crie seu primeiro hábito"),
        ["liftoff"] = ("Decolagem", "Complete seu primeiro hábito"),
        ["mission_control"] = ("Controle de Missão", "Crie sua primeira meta"),
        ["onboarding_complete"] = ("Tudo Pronto", "Conclua sua lista de configuração"),
        ["week_warrior"] = ("Guerreiro da Semana", "Alcance uma sequência de 7 dias"),
        ["fortnight_focus"] = ("Foco Quinzenal", "Alcance uma sequência de 14 dias"),
        ["monthly_master"] = ("Mestre Mensal", "Alcance uma sequência de 30 dias"),
        ["quarter_champion"] = ("Campeão Trimestral", "Alcance uma sequência de 90 dias"),
        ["centurion"] = ("Centurião", "Alcance uma sequência de 100 dias"),
        ["year_of_discipline"] = ("Ano de Disciplina", "Alcance uma sequência de 365 dias"),
        ["half_year_hero"] = ("Herói do Semestre", "Alcance uma sequência de 180 dias"),
        ["streak_titan"] = ("Titã da Sequência", "Alcance uma sequência de 500 dias"),
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
        ["first_cheer"] = ("Boas Energias", "Envie ou receba seu primeiro incentivo"),
        ["first_friend"] = ("Primeiro Amigo", "Adicione seu primeiro amigo"),
        ["squad_goals"] = ("Esquadrão Completo", "Alcance 5 amigos"),
        ["cheerleader"] = ("Líder de Torcida", "Envie 25 incentivos"),
        ["show_off"] = ("Exibido", "Compartilhe seu primeiro card"),
        ["year_in_review"] = ("Retrospectiva", "Veja sua primeira Retrospectiva"),
        ["team_player"] = ("Espírito de Equipe", "Entre no seu primeiro desafio cooperativo"),
        ["mission_accomplished"] = ("Missão Cumprida", "Complete um desafio cooperativo"),
        ["battle_buddy"] = ("Parceiro de Batalha", "Inicie uma dupla de responsabilidade"),
        ["streak_immortal"] = ("Sequência Imortal", "Alcance uma sequência de 1000 dias"),
        ["unstoppable"] = ("Imbatível", "Complete 2500 hábitos no total"),
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
        var levelTitle = isPt && LevelTranslationsPt.TryGetValue(Math.Min(newLevel.Level, LevelDefinitions.TableMaxLevel), out var ptTitle)
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
                await notifiers.PushService.SendToUserAsync(push.UserId, push.Title, push.Body, cancellationToken: ct);
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
