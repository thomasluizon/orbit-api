using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Chat;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

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

        var userToday = await execution.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var weekStartDay = await execution.UserDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var includeChecklists = request.ClientContext?.SupportsPendingOperationPlacement == true;
        var candidates = await data.HabitRepository.ProjectAsync(
            h => h.UserId == request.UserId,
            q => ChatHabitCandidate.Select(q, userToday, includeChecklists),
            cancellationToken);
        var userHabits = candidates.Select(candidate => candidate.ToHabit(request.UserId)).ToArray();
        var doneTodayIds = candidates.Where(candidate => candidate.CompletedToday)
            .Select(candidate => candidate.Schedule.Id).ToHashSet();
        var (promptHabitIndex, todayFacts) = await SelectPromptHabitIndexAsync(
            userHabits, userToday, weekStartDay, doneTodayIds, execution.ScheduleLogReader, cancellationToken);
        var promptIds = promptHabitIndex.Habits.Select(habit => habit.Id).ToHashSet();
        IReadOnlyList<Habit> promptHabits = promptIds.Count == 0 ? [] : await data.HabitRepository.FindAsync(
            h => h.UserId == request.UserId && promptIds.Contains(h.Id),
            q => q.Include(habit => habit.Tags), cancellationToken);
        var hydratedById = promptHabits.Where(habit => promptIds.Contains(habit.Id)).ToDictionary(habit => habit.Id);
        var hydratedPrompt = promptHabitIndex.Habits
            .Select(habit => hydratedById.GetValueOrDefault(habit.Id, habit)).ToList();
        var activeHabits = userHabits.Where(habit => !habit.IsCompleted).ToList();
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

        var activeGoals = await data.GoalRepository.FindAsync(
            g => g.UserId == request.UserId && g.Status == GoalStatus.Active,
            null,
            cancellationToken);

        var userTags = await data.TagRepository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        var checklistTemplateNames = await data.ChecklistTemplateRepository.ProjectAsync(
            template => template.UserId == request.UserId,
            query => query.Select(template => template.Name),
            cancellationToken);

        var enabledFeatureFlags = await data.FeatureFlagService.GetEnabledKeysForUserAsync(
            request.UserId,
            cancellationToken);

        dbStopwatch.Stop();
        LogContextLoaded(logger, dbStopwatch.ElapsedMilliseconds, activeHabits.Count);

        return Result.Success(new ChatContext(
            userHabits,
            activeHabits,
            hydratedPrompt,
            promptHabitIndex.IsPartial,
            user,
            ChatLocale.Resolve(request.ClientContext, user?.Language),
            hasProAccess,
            activeGoals,
            userTags,
            checklistTemplateNames,
            enabledFeatureFlags,
            userToday,
            todayFacts,
            dbStopwatch.ElapsedMilliseconds));
    }

    internal static async Task<(PromptHabitIndex Index, HabitTodaySnapshot Facts)> SelectPromptHabitIndexAsync(
        IReadOnlyList<Habit> habits, DateOnly today, int weekStartDay, IReadOnlySet<Guid> doneTodayIds,
        IHabitScheduleLogReader reader, CancellationToken ct)
    {
        var days = doneTodayIds.Select(id => new HabitScheduleLogDay(id, today, 1, 0, true)).ToArray();
        var facts = HabitTodaySnapshot.Build(habits, today, weekStartDay, days, new HashSet<Guid>());
        var needsHistory = habits.Where(habit =>
                habit.Schedule == HabitSchedule.Flexible && facts.TodayIds.Contains(habit.Id) && !doneTodayIds.Contains(habit.Id)
                || habit.Schedule == HabitSchedule.Recurring && facts.OverdueIds.Contains(habit.Id))
            .Select(habit => habit.Id).ToHashSet();
        while (true)
        {
            var index = BuildPromptHabitIndex(habits, today, facts);
            var unresolved = index.Habits.Where(habit => needsHistory.Contains(habit.Id)).ToArray();
            if (unresolved.Length == 0)
                return (index, facts);

            var resolved = await HabitTodaySnapshot.ReadAsync(unresolved, today, weekStartDay, reader, ct);
            var resolvedIds = unresolved.Select(habit => habit.Id).ToHashSet();
            facts = facts with
            {
                TodayIds = facts.TodayIds.Except(resolvedIds).Concat(resolved.TodayIds).ToHashSet(),
                OverdueIds = facts.OverdueIds.Except(resolvedIds).Concat(resolved.OverdueIds).ToHashSet()
            };
            needsHistory.ExceptWith(resolvedIds);
        }
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
        List<Habit> PromptHabits,
        bool IsPromptHabitIndexPartial,
        User? User,
        string? Language,
        bool HasProAccess,
        IReadOnlyList<Goal> ActiveGoals,
        IReadOnlyList<Tag> UserTags,
        IReadOnlyList<string> ChecklistTemplateNames,
        IReadOnlyList<string> EnabledFeatureFlags,
        DateOnly UserToday,
        HabitTodaySnapshot TodayFacts,
        long ContextLoadMilliseconds);
}
