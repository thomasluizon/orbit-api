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
        var promptHabits = BuildPromptHabitIndex(userHabits);
        var user = await data.UserRepository.GetByIdAsync(request.UserId, cancellationToken);
        var hasProAccess = user?.HasProAccess ?? false;
        var aiMemoryEnabled = user is { HasProAccess: true, AiMemoryEnabled: true };
        var userToday = await execution.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);

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

        var messageGate = await execution.PayGateService.CanSendAiMessage(request.UserId, cancellationToken);
        if (messageGate.IsFailure)
            return messageGate.PropagateError<ChatContext>();

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
            promptHabits,
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

    private static List<Habit> BuildPromptHabitIndex(IReadOnlyCollection<Habit> userHabits)
    {
        if (userHabits.Count == 0)
            return [];

        var habitsById = userHabits.ToDictionary(habit => habit.Id);
        var indexedHabitIds = new HashSet<Guid>();

        foreach (var habit in userHabits.Where(habit => !habit.IsCompleted))
        {
            var current = habit;

            while (indexedHabitIds.Add(current.Id) &&
                   current.ParentHabitId is Guid parentId &&
                   habitsById.TryGetValue(parentId, out var parent))
            {
                current = parent;
            }
        }

        return userHabits
            .Where(habit => indexedHabitIds.Contains(habit.Id))
            .ToList();
    }

    private sealed record ChatContext(
        List<Habit> ActiveHabits,
        List<Habit> PromptHabits,
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
