using System.Globalization;
using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateGoalTool(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IAiTool
{
    public string Name => "update_goal";

    public string Description =>
        "Update an existing goal's properties. Only include fields you want to change. Use this for renaming goals, editing descriptions, changing targets or units, and updating deadlines. To clear the deadline, set deadline to null.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            goal_id = new { type = JsonSchemaTypes.String, description = "ID of the goal to update." },
            title = new { type = JsonSchemaTypes.String, description = "New goal title.", nullable = true },
            description = new { type = JsonSchemaTypes.String, description = "New description. Set to null to clear.", nullable = true },
            target_value = new { type = "number", description = "New numeric target value." },
            unit = new { type = JsonSchemaTypes.String, description = "New unit of measurement." },
            deadline = new { type = JsonSchemaTypes.String, description = "New deadline in YYYY-MM-DD format. Set to null to clear.", nullable = true }
        },
        required = new[] { "goal_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("goal_id", out var goalIdEl)
            || !Guid.TryParse(goalIdEl.GetString(), out var goalId))
        {
            return new ToolResult(false, Error: "goal_id is required and must be a valid GUID.");
        }

        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == goalId && g.UserId == userId && !g.IsDeleted,
            cancellationToken: ct);

        if (goal is null)
            return new ToolResult(false, Error: $"Goal {goalId} not found.");

        var title = ResolveTitle(args, goal);
        var description = ResolveDescription(args, goal);
        var targetValue = ResolveTargetValue(args, goal);
        var unit = ResolveUnit(args, goal);
        var deadline = ResolveDeadline(args, goal);

        var result = goal.Update(title, description, targetValue, unit, deadline);
        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        await unitOfWork.SaveChangesAsync(ct);
        return new ToolResult(true, EntityId: goal.Id.ToString(), EntityName: goal.Title);
    }

    private static string ResolveTitle(JsonElement args, Goal goal) =>
        JsonArgumentParser.PropertyExists(args, "title")
            ? JsonArgumentParser.GetNullableString(args, "title") ?? goal.Title
            : goal.Title;

    private static string? ResolveDescription(JsonElement args, Goal goal) =>
        JsonArgumentParser.PropertyExists(args, "description")
            ? JsonArgumentParser.GetNullableString(args, "description")
            : goal.Description;

    private static decimal ResolveTargetValue(JsonElement args, Goal goal)
    {
        if (!args.TryGetProperty("target_value", out var targetValueEl)
            || targetValueEl.ValueKind != JsonValueKind.Number)
        {
            return goal.TargetValue;
        }

        return targetValueEl.GetDecimal();
    }

    private static string ResolveUnit(JsonElement args, Goal goal) =>
        JsonArgumentParser.PropertyExists(args, "unit")
            ? JsonArgumentParser.GetNullableString(args, "unit") ?? goal.Unit
            : goal.Unit;

    private static DateOnly? ResolveDeadline(JsonElement args, Goal goal)
    {
        if (!JsonArgumentParser.PropertyExists(args, "deadline"))
            return goal.Deadline;

        var deadlineString = JsonArgumentParser.GetNullableString(args, "deadline");
        if (deadlineString is null)
            return null;

        return DateOnly.TryParseExact(
            deadlineString,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var deadline)
            ? deadline
            : goal.Deadline;
    }
}
