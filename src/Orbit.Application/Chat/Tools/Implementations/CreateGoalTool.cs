using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class CreateGoalTool(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IAiTool
{
    public string Name => "create_goal";
    public string Description => "Create a new goal to track progress toward a target. Goals have a target value and unit (e.g., 'read 12 books', 'lose 5 kg'). Use when user wants to track measurable long-term progress.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            title = new { type = "STRING", description = "Name of the goal" },
            description = new { type = "STRING", description = "Optional description" },
            target_value = new { type = "NUMBER", description = "Target number to reach" },
            unit = new { type = "STRING", description = "Unit of measurement (e.g., 'books', 'kg', 'dollars')" },
            deadline = new { type = "STRING", description = "Optional deadline in YYYY-MM-DD format" }
        },
        required = new[] { "title", "target_value", "unit" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("title", out var titleEl) || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return new ToolResult(false, Error: "title is required.");
        if (!args.TryGetProperty("target_value", out var targetEl) || targetEl.ValueKind != JsonValueKind.Number)
            return new ToolResult(false, Error: "target_value is required and must be a number.");
        if (!args.TryGetProperty("unit", out var unitEl) || string.IsNullOrWhiteSpace(unitEl.GetString()))
            return new ToolResult(false, Error: "unit is required.");

        string? description = args.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String ? descEl.GetString() : null;
        DateOnly? deadline = null;
        if (args.TryGetProperty("deadline", out var dlEl) && dlEl.ValueKind == JsonValueKind.String && DateOnly.TryParse(dlEl.GetString(), out var parsed))
            deadline = parsed;

        var goalResult = Goal.Create(userId, titleEl.GetString()!, targetEl.GetDecimal(), unitEl.GetString()!, description, deadline);
        if (goalResult.IsFailure) return new ToolResult(false, Error: goalResult.Error);

        await goalRepository.AddAsync(goalResult.Value, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return new ToolResult(true, EntityId: goalResult.Value.Id.ToString(), EntityName: goalResult.Value.Title);
    }
}
