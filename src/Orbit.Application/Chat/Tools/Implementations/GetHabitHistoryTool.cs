using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class GetHabitHistoryTool(IMediator mediator) : IAiTool
{
    public string Name => "get_habit_history";
    public string Description => "Read actual dated habit logs for an absolute period. Values indicate completion records, never measured amounts.";
    public bool IsReadOnly => true;
    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String },
            date_from = new { type = JsonSchemaTypes.String },
            date_to = new { type = JsonSchemaTypes.String },
            offset = new { type = JsonSchemaTypes.Integer },
            limit = new { type = JsonSchemaTypes.Integer }
        },
        required = new[] { "habit_id", "date_from", "date_to" }
    };
    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!Guid.TryParse(JsonArgumentParser.GetOptionalString(args, "habit_id"), out var habitId)
            || JsonArgumentParser.ParseDateOnly(args, "date_from") is not { } from
            || JsonArgumentParser.ParseDateOnly(args, "date_to") is not { } to)
            return new(false, Error: "An owned habit and absolute date_from/date_to are required.");
        var result = await mediator.Send(new GetHabitLogsQuery(userId, habitId, from, to), ct);
        if (result.IsFailure) return ToolResult.FromFailure(result);
        var logs = result.Value.OrderByDescending(l => l.Date).ThenBy(l => l.Id).ToList();
        var offset = Math.Max(0, JsonArgumentParser.GetOptionalInt(args, "offset") ?? 0);
        var limit = Math.Clamp(JsonArgumentParser.GetOptionalInt(args, "limit") ?? 20, 1, 200);
        return new(true, Payload: new HabitHistoryRead(logs.Count,
            logs.Skip(offset).Take(limit).ToList(), ReadRevision.Hash(logs)));
    }
}

public sealed record HabitHistoryRead(int TotalCount, IReadOnlyList<HabitLogResponse> Logs, string SourceRevision);
