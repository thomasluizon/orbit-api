using System.Globalization;
using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class CreateGoalTool(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IAiTool
{
    public string Name => "create_goal";
    public string Description => "Create a new goal to track progress toward a target. Goals can have a target value and unit (e.g., 'read 12 books', 'lose 5 kg'). If the user doesn't specify a target, use target_value=1 and unit='goal'. Use when user wants to track measurable long-term progress. Use goal_type='Streak' to create a streak goal that tracks the habit's consecutive day streak.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            title = new { type = JsonSchemaTypes.String, description = "Name of the goal" },
            description = new { type = JsonSchemaTypes.String, description = "Optional description" },
            target_value = new { type = "number", description = "Target number to reach (default: 1)" },
            unit = new { type = JsonSchemaTypes.String, description = "Unit of measurement (e.g., 'books', 'kg', 'dollars', 'goal')" },
            deadline = new { type = JsonSchemaTypes.String, description = "Optional deadline in YYYY-MM-DD format" },
            goal_type = new { type = JsonSchemaTypes.String, description = "Goal type: 'Standard' (default) or 'Streak' (tracks consecutive habit streak)" }
        },
        required = new[] { "title" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("title", out var titleEl) || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return new ToolResult(false, Error: "title is required.");

        var targetValue = args.TryGetProperty("target_value", out var targetEl) && targetEl.ValueKind == JsonValueKind.Number
            ? targetEl.GetDecimal() : 1m;
        var unit = args.TryGetProperty("unit", out var unitEl) && !string.IsNullOrWhiteSpace(unitEl.GetString())
            ? unitEl.GetString()! : "goal";

        string? description = args.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String ? descEl.GetString() : null;
        DateOnly? deadline = null;
        if (args.TryGetProperty("deadline", out var dlEl) && dlEl.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(dlEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            deadline = parsed;

        var goalType = GoalType.Standard;
        if (args.TryGetProperty("goal_type", out var goalTypeEl) && goalTypeEl.ValueKind == JsonValueKind.String)
            Enum.TryParse(goalTypeEl.GetString(), ignoreCase: true, out goalType);

        var goalResult = Goal.Create(userId, titleEl.GetString() ?? string.Empty, targetValue, unit, description, deadline, type: goalType);
        if (goalResult.IsFailure) return new ToolResult(false, Error: goalResult.Error);

        await goalRepository.AddAsync(goalResult.Value, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return new ToolResult(true, EntityId: goalResult.Value.Id.ToString(), EntityName: goalResult.Value.Title);
    }
}
