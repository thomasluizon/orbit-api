using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Chat;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<Result<ChatContext>> LoadChatContextAsync(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        LogFetchingContext(logger);
        var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var userHabits = await data.HabitRepository.FindAsync(
            h => h.UserId == request.UserId,
            q => q,
            cancellationToken);
        var activeHabits = userHabits.Where(habit => !habit.IsCompleted).ToList();
        var userToday = await execution.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var weekStartDay = await execution.UserDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var logFrom = userToday.AddDays(-AppConstants.MaxRangeDays);
        var activeIds = activeHabits.Select(habit => habit.Id).ToArray();
        var logDays = await execution.ScheduleLogReader.ReadDaysAsync(
            activeIds, logFrom, userToday, cancellationToken);
        var logFacts = new HabitScheduleLogFacts(logDays);
        var dueDateResolution = activeHabits
            .Where(habit => habit.DueDate >= logFrom
                && habit.DueDate <= userToday
                && logFacts.ResolvedDates(habit.Id).Contains(habit.DueDate))
            .Select(habit => habit.Id)
            .ToHashSet();
        var oldDueDateIds = activeHabits
            .Where(habit => habit.FrequencyUnit is not null
                && !habit.IsFlexible
                && !habit.IsBadHabit
                && habit.DueDate < logFrom)
            .Select(habit => habit.Id)
            .ToArray();
        if (oldDueDateIds.Length > 0)
        {
            var olderResolutions = await execution.ScheduleLogReader.ReadResolvedDueDateIdsAsync(
                oldDueDateIds, cancellationToken);
            dueDateResolution.UnionWith(olderResolutions);
        }
        var todayFacts = HabitTodaySnapshot.Build(
            activeHabits, userToday, weekStartDay, logDays, dueDateResolution);
        var promptHabitIndex = BuildPromptHabitIndex(userHabits, userToday, todayFacts);
        if (promptHabitIndex.IsPartial)
        {
            LogPromptHabitIndexTruncated(
                logger,
                promptHabitIndex.OriginalEntryCount,
                promptHabitIndex.Habits.Count,
                AppConstants.MaxPromptHabitEntries);
        }
        var user = await data.UserRepository.GetByIdAsync(request.UserId, cancellationToken);
        var hasProAccess = user?.HasProAccess ?? false;
        var aiMemoryEnabled = user is { HasProAccess: true, AiMemoryEnabled: true };

        var freshProgressValues = await execution.GoalProgressReadSyncer.ComputeFreshValuesAsync(request.UserId, userToday, cancellationToken);
        var loadedGoals = await data.GoalRepository.FindAsync(
            g => g.UserId == request.UserId && g.Status == GoalStatus.Active,
            q => q.Include(g => g.Habits),
            cancellationToken);
        foreach (var goal in loadedGoals)
        {
            if (freshProgressValues.TryGetValue(goal.Id, out var fresh))
                GoalProgressSyncService.ApplyReadValue(goal, fresh);
        }
        IReadOnlyList<Goal> activeGoals = loadedGoals;

        IReadOnlyList<UserFact> userFacts = [];
        if (aiMemoryEnabled)
        {
            userFacts = await data.UserFactRepository.FindAsync(
                f => f.UserId == request.UserId,
                cancellationToken);
        }

        var userTags = await data.TagRepository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        var checklistTemplates = await data.ChecklistTemplateRepository.FindAsync(
            template => template.UserId == request.UserId,
            cancellationToken);

        var enabledFeatureFlags = await data.FeatureFlagService.GetEnabledKeysForUserAsync(
            request.UserId,
            cancellationToken);

        dbStopwatch.Stop();
        LogContextLoaded(logger, dbStopwatch.ElapsedMilliseconds, activeHabits.Count, userFacts.Count);

        return Result.Success(new ChatContext(
            activeHabits,
            promptHabitIndex.Habits,
            promptHabitIndex.IsPartial,
            user,
            hasProAccess,
            aiMemoryEnabled,
            activeGoals,
            userFacts,
            userTags,
            checklistTemplates,
            enabledFeatureFlags,
            userToday,
            todayFacts,
            dbStopwatch.ElapsedMilliseconds));
    }

    internal static PromptHabitIndex BuildPromptHabitIndex(
        IReadOnlyCollection<Habit> userHabits,
        DateOnly userToday,
        HabitTodaySnapshot? todayFacts = null)
    {
        todayFacts ??= HabitTodaySnapshot.FromLoadedHabits(userHabits.ToList(), userToday);
        if (userHabits.Count == 0)
            return new PromptHabitIndex([], false, 0, todayFacts.DoneTodayIds);

        var habitsById = userHabits.ToDictionary(habit => habit.Id);
        var allIndexedHabitIds = new HashSet<Guid>();

        foreach (var habit in userHabits.Where(habit => !habit.IsCompleted))
        {
            var current = habit;

            while (allIndexedHabitIds.Add(current.Id) &&
                   current.ParentHabitId is Guid parentId &&
                   habitsById.TryGetValue(parentId, out var parent))
            {
                current = parent;
            }
        }

        if (allIndexedHabitIds.Count <= AppConstants.MaxPromptHabitEntries)
        {
            return new PromptHabitIndex(
                userHabits.Where(habit => allIndexedHabitIds.Contains(habit.Id)).ToList(),
                false,
                allIndexedHabitIds.Count,
                todayFacts.DoneTodayIds);
        }

        var selectedHabitIds = new HashSet<Guid>();
        var prioritizedActiveHabits = userHabits
            .Where(habit => !habit.IsCompleted)
            .OrderBy(habit => GetPromptPriority(habit, todayFacts))
            .ThenBy(habit => habit.Position ?? int.MaxValue)
            .ThenBy(habit => habit.Id)
            .ToList();

        foreach (var habit in prioritizedActiveHabits)
        {
            var missingPath = BuildMissingHabitPath(habit, habitsById, selectedHabitIds);
            if (selectedHabitIds.Count + missingPath.Count > AppConstants.MaxPromptHabitEntries)
                continue;

            foreach (var pathHabit in missingPath)
                selectedHabitIds.Add(pathHabit.Id);

            if (selectedHabitIds.Count == AppConstants.MaxPromptHabitEntries)
                break;
        }

        return new PromptHabitIndex(
            userHabits.Where(habit => selectedHabitIds.Contains(habit.Id)).ToList(),
            true,
            allIndexedHabitIds.Count,
            todayFacts.DoneTodayIds);
    }

    private static int GetPromptPriority(Habit habit, HabitTodaySnapshot todayFacts)
    {
        if (todayFacts.OverdueIds.Contains(habit.Id))
            return 0;

        return todayFacts.TodayIds.Contains(habit.Id) ? 1 : 2;
    }

    private static List<Habit> BuildMissingHabitPath(
        Habit habit,
        IReadOnlyDictionary<Guid, Habit> habitsById,
        IReadOnlySet<Guid> selectedHabitIds)
    {
        var path = new List<Habit>();
        var visitedHabitIds = new HashSet<Guid>();
        var current = habit;

        while (visitedHabitIds.Add(current.Id))
        {
            if (!selectedHabitIds.Contains(current.Id))
                path.Add(current);

            if (current.ParentHabitId is not Guid parentId ||
                !habitsById.TryGetValue(parentId, out var parent))
            {
                break;
            }

            current = parent;
        }

        path.Reverse();
        return path;
    }

    internal sealed record PromptHabitIndex(
        List<Habit> Habits,
        bool IsPartial,
        int OriginalEntryCount,
        IReadOnlySet<Guid> DoneTodayHabitIds);

    private sealed record ChatContext(
        List<Habit> ActiveHabits,
        List<Habit> PromptHabits,
        bool IsPromptHabitIndexPartial,
        User? User,
        bool HasProAccess,
        bool AiMemoryEnabled,
        IReadOnlyList<Goal> ActiveGoals,
        IReadOnlyList<UserFact> UserFacts,
        IReadOnlyList<Tag> UserTags,
        IReadOnlyList<ChecklistTemplate> ChecklistTemplates,
        IReadOnlyList<string> EnabledFeatureFlags,
        DateOnly UserToday,
        HabitTodaySnapshot TodayFacts,
        long ContextLoadMilliseconds);
}
