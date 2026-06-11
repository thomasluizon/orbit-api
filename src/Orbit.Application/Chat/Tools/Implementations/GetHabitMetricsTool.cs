using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetHabitMetricsTool(IMediator mediator) : IAiTool
{
    public string Name => "get_habit_metrics";
    public bool IsReadOnly => true;

    public string Description =>
        "Read detailed metrics for a single habit: current and longest streak, completion rate, and total completions. Use this when the user asks how they are doing with one specific habit.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "The GUID of the habit to read metrics for." }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var habitId = JsonArgumentParser.GetOptionalString(args, "habit_id");
        if (!Guid.TryParse(habitId, out var parsedId))
            return new ToolResult(false, Error: "habit_id must be a valid GUID.");

        var result = await mediator.Send(new GetHabitMetricsQuery(userId, parsedId), ct);

        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}
