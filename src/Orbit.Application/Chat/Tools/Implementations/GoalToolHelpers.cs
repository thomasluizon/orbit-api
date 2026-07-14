using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

/// <summary>
/// Shared execution helpers for the goal-oriented AI tools: goal-id parsing and the
/// owner-scoped, not-soft-deleted goal lookup with its not-found guard. Centralizes the
/// boilerplate the goal tools would otherwise duplicate verbatim.
/// </summary>
internal static class GoalToolHelpers
{
    public static bool TryParseGoalId(JsonElement args, out Guid goalId)
    {
        goalId = Guid.Empty;
        return args.TryGetProperty("goal_id", out var goalIdEl)
            && Guid.TryParse(goalIdEl.GetString(), out goalId);
    }

    public static ToolResult InvalidGoalIdResult() =>
        new(false, Error: "goal_id is required and must be a valid GUID.");

    public static ToolResult GoalNotFoundResult(Guid goalId) =>
        new(false, Error: $"Goal {goalId} not found.");

    public static Task<Goal?> FindGoalAsync(
        IGenericRepository<Goal> goalRepository, Guid goalId, Guid userId, CancellationToken ct) =>
        goalRepository.FindOneTrackedAsync(
            g => g.Id == goalId && g.UserId == userId && !g.IsDeleted,
            cancellationToken: ct);
}
