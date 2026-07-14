using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Queries;

/// <summary>
/// The loaded goal together with the user's "today" used to build it and its projected
/// <see cref="GoalDetailDto"/>. Callers that also need goal metrics reuse <see cref="Goal"/> and
/// <see cref="UserToday"/> rather than reloading.
/// </summary>
internal record GoalDetailResult(Goal Goal, DateOnly UserToday, GoalDetailDto Dto);

/// <summary>
/// Loads a single owner-scoped goal with its progress logs and windowed habit logs, applies the
/// read-time streak sync, and projects the shared <see cref="GoalDetailDto"/>. Returns null when the
/// goal is not found so callers can surface their own typed failure.
/// </summary>
internal static class GoalDetailLoader
{
    public static async Task<GoalDetailResult?> BuildGoalDetailAsync(
        IGenericRepository<Goal> goalRepository,
        IUserDateService userDateService,
        Guid goalId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var userToday = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var streakWindowStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);

        var goals = await goalRepository.FindAsync(
            g => g.Id == goalId && g.UserId == userId,
            q => q.Include(g => g.ProgressLogs)
                  .Include(g => g.Habits).ThenInclude(h => h.Logs.Where(l => l.Date >= streakWindowStart)),
            cancellationToken);
        var goal = goals.Count > 0 ? goals[0] : null;

        if (goal is null)
            return null;

        GoalStreakSyncService.ApplyReadValue(goal, userToday);

        var progressPercentage = goal.TargetValue > 0
            ? Math.Min(100, Math.Round(goal.CurrentValue / goal.TargetValue * 100, 1))
            : 0;

        var progressHistory = goal.ProgressLogs
            .OrderByDescending(l => l.CreatedAtUtc)
            .Select(l => new GoalProgressEntryDto(l.Value, l.PreviousValue, l.Note, l.CreatedAtUtc))
            .ToList();

        var linkedHabits = goal.Habits
            .Select(h => new LinkedHabitDto(h.Id, h.Title))
            .ToList();

        var dto = new GoalDetailDto(
            goal.Id, goal.Title, goal.Description, goal.TargetValue, goal.CurrentValue,
            goal.Unit, goal.Status, goal.Type, goal.Deadline, goal.Position, goal.CreatedAtUtc,
            goal.CompletedAtUtc, progressPercentage, progressHistory, linkedHabits);

        return new GoalDetailResult(goal, userToday, dto);
    }
}
