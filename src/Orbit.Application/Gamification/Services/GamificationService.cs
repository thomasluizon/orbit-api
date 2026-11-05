using Microsoft.EntityFrameworkCore;
using Hangfire;
using Orbit.Application.Gamification.Jobs;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Habits.Services;
using Orbit.Application.Notifications;
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
    IGenericRepository<Notification> NotificationRepository,
    IGenericRepository<XpAwardLog> XpAwardLogRepository,
    IFoundingAchievementReader FoundingAchievementReader);

public partial class GamificationService(
    GamificationRepositories repos,
    IBackgroundJobClient backgroundJobs,
    IUserDateService userDateService,
    IXpAwarder xpAwarder,
    IUnitOfWork unitOfWork,
    IFeatureFlagService featureFlagService,
    ILogger<GamificationService> logger,
    HabitScheduleSnapshotStore scheduleSnapshots) : IGamificationService
{

    private static readonly string[] FoundingAchievementIds =
    [
        AchievementDefinitions.Liftoff,
        AchievementDefinitions.FirstOrbit,
        AchievementDefinitions.MissionControl,
        AchievementDefinitions.GoalCrusher,
        AchievementDefinitions.OnboardingComplete
    ];

    private sealed record PendingPush(Guid UserId, string Title, string Body, string Url);

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
        IReadOnlyDictionary<Guid, Habit>? batch = HabitLogBatchReadScope.For(userId)?.Habits;
        return await ConcurrencyRetry.RunAsync(unitOfWork, async ct =>
        {
            var loaded = batch;
            batch = null;
            var pushes = new List<PendingPush>();
            var outcome = await ComputeHabitsLoggedAsync(userId, habitIds, pushes, ct, loaded);
            if (!outcome.ShouldSave)
                return outcome.Results;

            await unitOfWork.SaveChangesAsync(ct);
            EnqueuePushes(pushes);
            return outcome.Results;
        }, ct, IsRetryableGamificationConflict);
    }

    private async Task<HabitsLoggedOutcome> ComputeHabitsLoggedAsync(
        Guid userId, IReadOnlyList<Guid> habitIds, List<PendingPush> pushes, CancellationToken ct,
        IReadOnlyDictionary<Guid, Habit>? loaded)
    {
        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null || !await IsGamificationUnlockedAsync(user, ct))
            return new HabitsLoggedOutcome([], ShouldSave: false);

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var today = await userDateService.GetUserTodayAsync(userId, ct);

        var loggedHabits = loaded is not null && habitIds.All(loaded.ContainsKey)
            ? habitIds.Select(id => loaded[id]).ToList()
            : (await repos.HabitRepository.FindTrackedAsync(
                h => h.UserId == userId && habitIds.Contains(h.Id), ct)).ToList();
        if (loggedHabits.Count == 0) return new HabitsLoggedOutcome([], ShouldSave: false);
        var zone = TimeZoneHelper.FindTimeZone(user.TimeZone);
        var metricLogs = await HabitStreakLogLoader.LoadAsync(repos.HabitLogRepository, loggedHabits, today, user.WeekStartDay, zone, ct);
        var loadedHabitIds = loggedHabits.Select(h => h.Id).ToArray();
        var todayRows = await repos.HabitLogRepository.ProjectAsync(l => loadedHabitIds.Contains(l.HabitId) && l.Date == today && l.Value > 0,
            q => q.Select(l => new LoggedHabitLog(l.HabitId, l.Id, l.Date, l.Value, false)), ct);
        var logsByHabit = metricLogs.SelectMany(pair => pair.Value.Select(l => new LoggedHabitLog(l.HabitId,
            l.Date == today ? todayRows.FirstOrDefault(row => row.HabitId == l.HabitId)?.Id ?? Guid.Empty : Guid.Empty,
            l.Date, l.Value, false))).ToLookup(l => l.HabitId);

        var context = await LoadLoggedHabitsContext(user, earned, today, ct);

        var results = new List<HabitLogGamificationResult>();
        foreach (var habitId in habitIds)
        {
            var habit = loggedHabits.FirstOrDefault(h => h.Id == habitId);
            if (habit is null) continue;

            results.Add(await ProcessLoggedHabit(user, habit, logsByHabit[habitId].ToList(), earned, context, today, pushes, ct));
        }

        return new HabitsLoggedOutcome(results, ShouldSave: true);
    }

    private sealed record LoggedHabitsContext(
        IReadOnlyList<Habit> AllUserHabits,
        IReadOnlySet<(Guid HabitId, DateOnly Date)> CompletedDates,
        TimeZoneInfo UserTimeZone,
        int TotalLogCount,
        TimeOfDayCounts TimeOfDayCounts,
        bool HasActivityInPriorWeek);

    private async Task<LoggedHabitsContext> LoadLoggedHabitsContext(
        User user, HashSet<string> earned, DateOnly today, CancellationToken ct)
    {
        var perfectStreakCutoff = today.AddDays(-AchievementChecks.PerfectStreakWindowDays);
        var allUserHabits = (await scheduleSnapshots.GetAsync(user.Id, ct))
            .Where(snapshot => !snapshot.IsCompleted && snapshot.Schedule != HabitSchedule.General && snapshot.ParentHabitId == null)
            .Select(snapshot => Habit.FromScheduleSnapshot(snapshot, user.Id))
            .ToList();
        var perfectHabitIds = allUserHabits.Select(h => h.Id).ToList();
        var completedDates = (await repos.HabitLogRepository.ProjectAsync(
            log => perfectHabitIds.Contains(log.HabitId) && log.Date >= perfectStreakCutoff && log.Date <= today,
            query => query.Select(log => new HabitCompletionDate(log.HabitId, log.Date)).Distinct(), ct))
            .Select(log => (log.HabitId, log.Date))
            .ToHashSet();

        var allHabitIds = await repos.HabitRepository.ProjectAsync(
            h => h.UserId == user.Id,
            query => query.Select(habit => habit.Id), ct);

        var totalLogCutoff = today.AddDays(-AppConstants.TotalCompletionWindowDays);
        var totalLogCount = earned.Contains(AchievementDefinitions.Liftoff) && earned.Contains(AchievementDefinitions.Unstoppable)
            ? 0
            : await repos.HabitLogRepository.CountAsync(
                l => allHabitIds.Contains(l.HabitId) && l.Date >= totalLogCutoff, ct);

        var timeOfDayCounts = await TimeOfDayAchievementCounts.ReadAsync(repos.HabitLogRepository, allHabitIds,
            earned, TimeZoneHelper.FindTimeZone(user.TimeZone), ct);

        var comebackCutoff = today.AddDays(-7);
        var hasActivityInPriorWeek = earned.Contains(AchievementDefinitions.Comeback)
            || await repos.HabitLogRepository.AnyAsync(
                l => allHabitIds.Contains(l.HabitId) && l.Date >= comebackCutoff && l.Date < today, ct);

        return new LoggedHabitsContext(
            allUserHabits,
            completedDates,
            TimeZoneHelper.FindTimeZone(user.TimeZone),
            totalLogCount,
            timeOfDayCounts,
            hasActivityInPriorWeek);
    }

    private async Task<HabitLogGamificationResult> ProcessLoggedHabit(
        User user, Habit habit, IReadOnlyList<LoggedHabitLog> logs, HashSet<string> earned, LoggedHabitsContext context, DateOnly today,
        List<PendingPush> pushes, CancellationToken ct)
    {
        var previousLevel = user.Level;
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();

        var metrics = HabitMetricsCalculator.CalculateProjected(habit,
            logs.Select(log => new HabitMetricLog(log.HabitId, log.Date, log.Value, log.IsDeleted)).ToList(),
            today, user.WeekStartDay, context.UserTimeZone);

        habit.RecordLongestStreak(metrics.LongestStreak);
        var xpEarned = habit.IsBadHabit
            ? 0
            : await AwardLoggedHabitXpAndAchievementsAsync(
                user, habit, logs, new AchievementAccumulator(earned, newAchievements), context, today, metrics.CurrentStreak, ct);

        if (habit.IsBadHabit
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

    private async Task<int> AwardLoggedHabitXpAndAchievementsAsync(
        User user, Habit habit, IReadOnlyList<LoggedHabitLog> logs, AchievementAccumulator accumulator, LoggedHabitsContext context, DateOnly today,
        int currentStreak, CancellationToken ct)
    {
        var (earned, newAchievements) = accumulator;
        var xp = 10 + currentStreak;
        var habitLogId = logs
            .Where(l => l.Date == today && l.Value > 0)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefault();
        await AwardXpAsync(user, xp, XpAwardSource.HabitLog, habitLogId, awardedAtUtc: DateTime.UtcNow, ct);

        if (!earned.Contains(AchievementDefinitions.Liftoff) && context.TotalLogCount == 1)
            AchievementChecks.TryGrant(AchievementDefinitions.Liftoff, user, earned, newAchievements);

        AchievementChecks.CheckConsistencyAchievements(currentStreak, earned, user, newAchievements);
        if (habit.LongestRecordedStreak >= AchievementChecks.TargetFor(AchievementDefinitions.StreakImmortal))
            AchievementChecks.TryGrant(AchievementDefinitions.StreakImmortal, user, earned, newAchievements);

        if (!earned.Contains(AchievementDefinitions.Unstoppable))
            AchievementChecks.CheckVolumeAchievements(context.TotalLogCount, earned, user, newAchievements);

        AchievementChecks.CheckPerfectDay(context.AllUserHabits, today, earned, user, newAchievements, context.CompletedDates);

        if (earned.Contains(AchievementDefinitions.PerfectDay)
            || newAchievements.Any(a => a.Definition.Id == AchievementDefinitions.PerfectDay))
        {
            AchievementChecks.CheckPerfectWeekAndMonth(context.AllUserHabits, today, earned, user, newAchievements, context.CompletedDates);
        }

        AchievementChecks.CheckTimeBasedAchievements(user, earned, newAchievements, context.TimeOfDayCounts.Early, context.TimeOfDayCounts.Night);

        if (!earned.Contains(AchievementDefinitions.Comeback) && !context.HasActivityInPriorWeek)
            AchievementChecks.TryGrant(AchievementDefinitions.Comeback, user, earned, newAchievements);

        return xp;
    }

    public async Task ProcessHabitCreated(Guid userId, CancellationToken ct = default)
    {
        await ProcessGamificationEventAsync(userId, async (user, earned, newAchievements) =>
        {
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
            var goalCount = await repos.GoalRepository.CountAsync(g => g.UserId == userId, ct);

            if (!earned.Contains(AchievementDefinitions.MissionControl) && goalCount == 1)
                AchievementChecks.TryGrant(AchievementDefinitions.MissionControl, user, earned, newAchievements);

            if (!earned.Contains(AchievementDefinitions.GoalSetter) && goalCount >= 3)
                AchievementChecks.TryGrant(AchievementDefinitions.GoalSetter, user, earned, newAchievements);
        }, ct);
    }

    public async Task ProcessGoalCompleted(Guid userId, Guid goalId, CancellationToken ct = default)
    {
        var wasAlreadyAwarded = await repos.XpAwardLogRepository.AnyAsync(
            award => award.UserId == userId
                     && award.Source == XpAwardSource.GoalCompleted
                     && award.SourceId == goalId,
            ct);
        if (wasAlreadyAwarded)
            return;

        await ProcessGamificationEventAsync(userId, async (user, earned, newAchievements) =>
        {
            await AwardXpAsync(user, 100, XpAwardSource.GoalCompleted, goalId, awardedAtUtc: DateTime.UtcNow, ct);

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

    public async Task ProcessOnboardingChecklistAsync(
        Guid userId, OnboardingChecklistSignal signal, CancellationToken ct = default)
    {
        await ConcurrencyRetry.RunAsync(unitOfWork, async ct =>
        {
            var pushes = new List<PendingPush>();
            var shouldSave = await ComputeOnboardingChecklistAsync(userId, signal, pushes, ct);
            if (!shouldSave)
                return false;

            await unitOfWork.SaveChangesAsync(ct);
            EnqueuePushes(pushes);
            return true;
        }, ct, IsRetryableGamificationConflict);
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

        if (!await IsGamificationUnlockedAsync(user, ct))
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReconcileFoundingAchievementsAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        return await ConcurrencyRetry.RunAsync(unitOfWork, async ct =>
        {
            var pushes = new List<PendingPush>();
            var granted = await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync(ClosedMonthRecapLock.ForUser(userId), transactionCt);
                return await ComputeFoundingReconciliationAsync(userId, pushes, transactionCt);
            }, ct);
            EnqueuePushes(pushes);
            return granted;
        }, ct, IsRetryableGamificationConflict);
    }

    public async Task<IReadOnlyList<FoundingReconciliationResult>> ReconcileFoundingAchievementPageAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var pushes = new List<PendingPush>();
        var results = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            pushes.Clear();
            var ids = userIds.Distinct().Order().ToList();
            foreach (var id in ids)
                await unitOfWork.AcquireAdvisoryLockAsync(ClosedMonthRecapLock.ForUser(id), token);
            var evidence = await repos.FoundingAchievementReader.ReadEvidencePageAsync(ids, token);
            var outcomes = new List<FoundingReconciliationResult>(ids.Count);
            foreach (var id in ids)
            {
                if (!evidence.TryGetValue(id, out var found)) continue;
                var pending = new List<PendingPush>();
                try
                {
                    var granted = await unitOfWork.ExecuteInSavepointAsync(
                        inner => ComputeFoundingReconciliationAsync(id, pending, inner, found), token);
                    outcomes.Add(new(id, granted));
                    pushes.AddRange(pending);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    outcomes.Add(new(id, [], exception));
                }
            }
            return (IReadOnlyList<FoundingReconciliationResult>)outcomes;
        }, ct);
        EnqueuePushes(pushes);
        return results;
    }

    private async Task<IReadOnlyList<string>> ComputeFoundingReconciliationAsync(
        Guid userId,
        List<PendingPush> pushes,
        CancellationToken ct, FoundingAchievementEvidence? prefetchedEvidence = null)
    {
        if (!await IsGamificationUnlockedForUserAsync(userId, ct))
            return [];

        var earned = await LoadEarnedAchievementIds(userId, ct);
        if (FoundingAchievementIds.All(earned.Contains))
            return [];

        var evidence = prefetchedEvidence ?? await repos.FoundingAchievementReader.ReadEvidenceAsync(userId, ct);
        if (evidence is null)
            return [];

        var eligible = new List<string>(FoundingAchievementIds.Length);
        AddIfEligible(AchievementDefinitions.Liftoff, evidence.HasHabitLog, earned, eligible);
        AddIfEligible(AchievementDefinitions.FirstOrbit, evidence.HasTopLevelHabit, earned, eligible);
        AddIfEligible(AchievementDefinitions.MissionControl, evidence.HasGoal, earned, eligible);
        AddIfEligible(AchievementDefinitions.GoalCrusher, evidence.HasCompletedGoal, earned, eligible);
        AddIfEligible(
            AchievementDefinitions.OnboardingComplete,
            evidence.HasCompletedOnboardingChecklist,
            earned,
            eligible);

        if (eligible.Count == 0 || !await IsGamificationUnlockedForUserAsync(userId, ct))
            return [];

        var granted = await ComputeGrantAchievementsAsync(userId, eligible, pushes, ct);
        if (granted.Count > 0)
            await unitOfWork.SaveChangesAsync(ct);

        return granted;
    }

    private static void AddIfEligible(
        string achievementId,
        bool isEligible,
        HashSet<string> earned,
        List<string> eligible)
    {
        if (isEligible && !earned.Contains(achievementId))
            eligible.Add(achievementId);
    }

    private async Task<bool> IsGamificationUnlockedForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await repos.UserRepository.FindOneTrackedAsync(
            candidate => candidate.Id == userId,
            cancellationToken: ct);
        return user is not null && await IsGamificationUnlockedAsync(user, ct);
    }

    public async Task<IReadOnlyList<string>> TryGrantAchievementsAsync(
        Guid userId, IReadOnlyList<string> achievementIds, CancellationToken ct = default)
    {
        return await ConcurrencyRetry.RunAsync(unitOfWork, async ct =>
        {
            var pushes = new List<PendingPush>();
            var granted = await ComputeGrantAchievementsAsync(userId, achievementIds, pushes, ct);
            if (granted.Count == 0)
                return granted;

            await unitOfWork.SaveChangesAsync(ct);

            EnqueuePushes(pushes);
            return granted;
        }, ct, IsRetryableGamificationConflict);
    }

    /// <summary>
    /// Idempotently grants the requested achievements to the loaded user through the audited XP,
    /// level, and notification funnel. A concurrent duplicate retries against persisted state.
    /// </summary>
    private async Task<IReadOnlyList<string>> ComputeGrantAchievementsAsync(
        Guid userId,
        IReadOnlyList<string> achievementIds,
        List<PendingPush> pushes,
        CancellationToken ct)
    {
        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null)
            return [];

        var earned = await LoadEarnedAchievementIds(userId, ct);
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        var previousLevel = user.Level;

        foreach (var achievementId in achievementIds)
            AchievementChecks.TryGrant(achievementId, user, earned, newAchievements);

        if (newAchievements.Count == 0)
            return [];

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
    /// load user, check gamification availability, load earned achievements, run domain-specific checks,
    /// persist achievements, update level, send notifications, save changes.
    /// </summary>
    private async Task ProcessGamificationEventAsync(
        Guid userId,
        Func<User, HashSet<string>, List<(UserAchievement Entity, AchievementDefinition Definition)>, Task> checkAchievements,
        CancellationToken ct)
    {
        await ConcurrencyRetry.RunAsync(unitOfWork, async ct =>
        {
            var pushes = new List<PendingPush>();
            var shouldSave = await ComputeGamificationEventAsync(userId, checkAchievements, pushes, ct);
            if (!shouldSave) return false;
            await unitOfWork.SaveChangesAsync(ct);
            EnqueuePushes(pushes);
            return true;
        }, ct, IsRetryableGamificationConflict);
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
    /// Whether gamification earning, including streaks, XP, levels, and achievements, is active for the user:
    /// always for Pro, and for free users once the <see cref="FeatureFlagKeys.GamificationFreeTier"/> flag is enabled.
    /// </summary>
    private async Task<bool> IsGamificationUnlockedAsync(User user, CancellationToken ct)
    {
        if (user.HasProAccess) return true;
        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(user.Id, ct);
        return GamificationAccess.IsUnlocked(user, enabledFlags);
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
        }
    }

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
        var name = isPt ? achievement.NamePt ?? achievement.Name : achievement.Name;
        var description = isPt ? achievement.DescriptionPt ?? achievement.Description : achievement.Description;

        var embeddedName = char.ToLowerInvariant(name[0]) + name[1..];
        var title = isPt
            ? $"Nova conquista: {embeddedName}"
            : $"New achievement: {embeddedName}";
        var body = $"{description} (+{achievement.XpReward} XP)";

        var notification = Notification.Create(userId, title, body, NotificationUrls.Progress);
        await repos.NotificationRepository.AddAsync(notification, ct);

        pushes.Add(new PendingPush(userId, title, body, NotificationUrls.Progress));
    }

    private async Task QueueLevelUpNotification(
        Guid userId, LevelDefinition newLevel, string? language, List<PendingPush> pushes, CancellationToken ct)
    {
        var isPt = LocaleHelper.IsPortuguese(language);
        var title = isPt
            ? $"Você chegou ao nível {newLevel.Level}"
            : $"You reached level {newLevel.Level}";
        var levelTitle = isPt && LevelTranslationsPt.TryGetValue(Math.Min(newLevel.Level, LevelDefinitions.TableMaxLevel), out var ptTitle)
            ? ptTitle : newLevel.Title;
        var body = isPt
            ? $"O nível {newLevel.Level} se chama {levelTitle}."
            : $"Level {newLevel.Level} is called {levelTitle}.";

        var notification = Notification.Create(userId, title, body, NotificationUrls.Progress);
        await repos.NotificationRepository.AddAsync(notification, ct);

        pushes.Add(new PendingPush(userId, title, body, NotificationUrls.Progress));
    }

    private static bool IsRetryableGamificationConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || exception is DbUpdateException updateException && DbUniqueViolation.IsUniqueViolation(updateException);

    private void EnqueuePushes(IReadOnlyList<PendingPush> pushes)
    {
        foreach (var push in pushes)
            unitOfWork.AfterCommit(() =>
            {
                try
                {
                    backgroundJobs.Enqueue<SendGamificationPushJob>(job => job.ExecuteAsync(push.UserId, push.Title, push.Body, push.Url));
                }
                catch (Exception exception)
                {
                    LogPushNotificationFailed(logger, exception, push.UserId);
                }
            });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to enqueue gamification push for user {UserId}")]
    private static partial void LogPushNotificationFailed(ILogger logger, Exception ex, Guid userId);
}
