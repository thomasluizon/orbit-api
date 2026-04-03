using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class LinkHabitsToGoalTool(
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IAiTool
{
    public string Name => "link_habits_to_goal";

    public string Description =>
        "Link one or more habits to a goal. Replaces all existing habit links for the goal. Use when user wants to associate habits with a goal.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            goal_id = new { type = JsonSchemaTypes.String, description = "ID of the goal to link habits to" },
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                description = "IDs of habits to link to the goal. Replaces all existing links.",
                items = new { type = JsonSchemaTypes.String }
            }
        },
        required = new[] { "goal_id", "habit_ids" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("goal_id", out var goalIdEl) ||
            !Guid.TryParse(goalIdEl.GetString(), out var goalId))
            return new ToolResult(false, Error: "goal_id is required and must be a valid GUID.");

        if (!args.TryGetProperty("habit_ids", out var habitIdsEl) || habitIdsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "habit_ids is required and must be an array.");

        var habitIds = new List<Guid>();
        foreach (var item in habitIdsEl.EnumerateArray())
        {
            if (Guid.TryParse(item.GetString(), out var id))
                habitIds.Add(id);
        }

        if (habitIds.Count > AppConstants.MaxHabitsPerGoal)
            return new ToolResult(false, Error: $"A goal can have at most {AppConstants.MaxHabitsPerGoal} linked habits.");

        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == goalId && g.UserId == userId,
            q => q.Include(g => g.Habits),
            ct);

        if (goal is null)
            return new ToolResult(false, Error: ErrorMessages.GoalNotFound);

        var habits = await habitRepository.FindTrackedAsync(
            h => habitIds.Contains(h.Id) && h.UserId == userId,
            ct);

        // Clear existing and reassign
        foreach (var existing in goal.Habits.ToList())
            goal.RemoveHabit(existing);

        foreach (var habit in habits)
            goal.AddHabit(habit);

        await unitOfWork.SaveChangesAsync(ct);

        return new ToolResult(true, EntityId: goal.Id.ToString(), EntityName: goal.Title);
    }
}
