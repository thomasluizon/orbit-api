using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
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
        var promptHabitIndex = BuildPromptHabitIndex(userHabits, userToday);
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

        IReadOnlyList<Goal> activeGoals = [];
        if (hasProAccess)
        {
            var freshStreakValues = await execution.StreakGoalReadSyncer.ComputeFreshValuesAsync(request.UserId, userToday, cancellationToken);
            var loadedGoals = await data.GoalRepository.FindAsync(
                g => g.UserId == request.UserId && g.Status == GoalStatus.Active,
                q => q.Include(g => g.Habits),
                cancellationToken);
            foreach (var goal in loadedGoals)
            {
                if (freshStreakValues.TryGetValue(goal.Id, out var fresh))
                    goal.SyncStreakProgress(fresh, allowCompletion: false);
            }
            activeGoals = loadedGoals;
        }

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
            dbStopwatch.ElapsedMilliseconds));
    }

    internal static PromptHabitIndex BuildPromptHabitIndex(
        IReadOnlyCollection<Habit> userHabits,
        DateOnly userToday)
    {
        if (userHabits.Count == 0)
            return new PromptHabitIndex([], false, 0);

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
                allIndexedHabitIds.Count);
        }

        var selectedHabitIds = new HashSet<Guid>();
        var prioritizedActiveHabits = userHabits
            .Where(habit => !habit.IsCompleted)
            .OrderBy(habit => GetPromptPriority(habit, userToday))
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
            allIndexedHabitIds.Count);
    }

    private static int GetPromptPriority(Habit habit, DateOnly userToday)
    {
        if (!habit.IsGeneral && habit.DueDate < userToday)
            return 0;

        return !habit.IsGeneral && habit.DueDate == userToday ? 1 : 2;
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
        int OriginalEntryCount);

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
        long ContextLoadMilliseconds);
}
