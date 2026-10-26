using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Chat;
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
        using var phase = ChatTurnTimer.Measure(ChatPhase.ContextLoad);
        LogFetchingContext(logger);
        var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var userHabits = await data.HabitRepository.FindAsync(
            h => h.UserId == request.UserId,
            q => q.Include(habit => habit.Tags),
            cancellationToken);
        var activeHabits = userHabits.Where(habit => !habit.IsCompleted).ToList();
        var userToday = await execution.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var weekStartDay = await execution.UserDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var todayFacts = await HabitTodaySnapshot.ReadAsync(
            userHabits, userToday, weekStartDay, execution.ScheduleLogReader, cancellationToken);
        var todayHabits = userHabits.Where(habit => !habit.IsCompleted
            || (habit.Schedule is HabitSchedule.OneTime or HabitSchedule.General) && todayFacts.DoneTodayIds.Contains(habit.Id)).ToList();
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
        LogContextLoaded(logger, dbStopwatch.ElapsedMilliseconds, activeHabits.Count);

        return Result.Success(new ChatContext(
            userHabits,
            activeHabits,
            todayHabits,
            promptHabitIndex.Habits,
            promptHabitIndex.IsPartial,
            user,
            hasProAccess,
            activeGoals,
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

        foreach (var habit in userHabits.Where(habit => !habit.IsCompleted
            || (habit.Schedule is HabitSchedule.OneTime or HabitSchedule.General) && todayFacts.DoneTodayIds.Contains(habit.Id)))
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
            .Where(habit => !habit.IsCompleted
                || (habit.Schedule is HabitSchedule.OneTime or HabitSchedule.General) && todayFacts.DoneTodayIds.Contains(habit.Id))
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
        IReadOnlyList<Habit> UserHabits,
        List<Habit> ActiveHabits,
        List<Habit> TodayHabits,
        List<Habit> PromptHabits,
        bool IsPromptHabitIndexPartial,
        User? User,
        bool HasProAccess,
        IReadOnlyList<Goal> ActiveGoals,
        IReadOnlyList<Tag> UserTags,
        IReadOnlyList<ChecklistTemplate> ChecklistTemplates,
        IReadOnlyList<string> EnabledFeatureFlags,
        DateOnly UserToday,
        HabitTodaySnapshot TodayFacts,
        long ContextLoadMilliseconds);
}
