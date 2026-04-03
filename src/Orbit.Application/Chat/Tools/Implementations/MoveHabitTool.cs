using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class MoveHabitTool(
    IGenericRepository<Habit> habitRepository) : IAiTool
{
    public string Name => "move_habit";

    public string Description =>
        "Move a habit under a different parent, or make it a top-level habit by passing null as new_parent_id.";

    public object GetParameterSchema() => new
    {
        type = "object",
        properties = new
        {
            habit_id = new { type = "string", description = "ID of the habit to move" },
            new_parent_id = new { type = "string", description = "ID of the new parent habit, or null to make top-level", nullable = true }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId && h.UserId == userId,
            cancellationToken: ct);

        if (habit is null)
            return new ToolResult(false, Error: $"Habit {habitId} not found.");

        Guid? newParentId = null;
        if (args.TryGetProperty("new_parent_id", out var parentEl)
            && parentEl.ValueKind == JsonValueKind.String
            && Guid.TryParse(parentEl.GetString(), out var parsedParentId))
        {
            // Verify the new parent exists and belongs to the user
            var parent = await habitRepository.FindOneTrackedAsync(
                h => h.Id == parsedParentId && h.UserId == userId,
                cancellationToken: ct);

            if (parent is null)
                return new ToolResult(false, Error: $"New parent habit {parsedParentId} not found.");

            // Prevent direct self-reference
            if (parsedParentId == habitId)
                return new ToolResult(false, Error: "A habit cannot be its own parent.");

            // Prevent deep circular reference: walk the parent chain of the target parent
            // to ensure we never reach the habit being moved
            if (await WouldCreateCycleAsync(habitId, parsedParentId, userId, ct))
                return new ToolResult(false, Error: "Cannot move habit: this would create a circular parent chain.");

            newParentId = parsedParentId;
        }

        habit.SetParentHabitId(newParentId);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }

    /// <summary>
    /// Walks up the ancestor chain of <paramref name="candidateParentId"/> and returns true
    /// if <paramref name="habitId"/> appears anywhere in that chain, which would create a cycle.
    /// </summary>
    private async Task<bool> WouldCreateCycleAsync(Guid habitId, Guid candidateParentId, Guid userId, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        var current = candidateParentId;

        while (true)
        {
            if (!visited.Add(current))
                break; // loop in existing data - stop to avoid infinite loop

            var ancestors = await habitRepository.FindAsync(
                h => h.Id == current && h.UserId == userId,
                ct);
            var ancestor = ancestors.Count > 0 ? ancestors[0] : null;

            if (ancestor is null)
                break;

            if (ancestor.ParentHabitId is null)
                break;

            if (ancestor.ParentHabitId.Value == habitId)
                return true;

            current = ancestor.ParentHabitId.Value;
        }

        return false;
    }
}
