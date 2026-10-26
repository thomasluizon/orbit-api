using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class GetPeriodAnalyticsTool(IMediator mediator) : IAiTool
{
    public string Name => "get_period_analytics";
    public bool IsReadOnly => true;
    public string Description => "Read exact-period totals or comparisons without AI narrative. Logs store counts, never measured quantities: for minutes, pages or litres use the closest named count metric and explain its unit. Calendar months require their exact first and last dates. Use completedHabits (unit habits) for how many habits did I complete or quantos hábitos concluí: distinct positive recurring habits, including independent children, excluding tasks and slips. Use positiveLogRecords (unit records) for how many times or quantas vezes. For supported period blocks, write at most one short sentence in the person's language without figures, then the block.";
    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            date_from = new { type = JsonSchemaTypes.String },
            date_to = new { type = JsonSchemaTypes.String },
            baseline_from = new { type = JsonSchemaTypes.String },
            baseline_to = new { type = JsonSchemaTypes.String },
            metric = new { type = JsonSchemaTypes.String, @enum = PeriodAnalyticsCalculator.Metrics },
            habit_id = new { type = JsonSchemaTypes.String },
            search = new { type = JsonSchemaTypes.String },
            tag = new { type = JsonSchemaTypes.String },
            is_bad_habit = new { type = JsonSchemaTypes.Boolean }
        },
        required = new[] { "date_from", "date_to", "metric" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var from = JsonArgumentParser.ParseDateOnly(args, "date_from");
        var to = JsonArgumentParser.ParseDateOnly(args, "date_to");
        if (!from.HasValue || !to.HasValue)
            return new(false, Error: "date_from and date_to must be absolute dates.");
        var id = JsonArgumentParser.GetOptionalString(args, "habit_id");
        if (id is not null && !Guid.TryParse(id, out _))
            return new(false, Error: "habit_id must be a valid GUID.");
        var result = await mediator.Send(new GetPeriodAnalyticsQuery(userId, from.Value, to.Value,
            JsonArgumentParser.GetOptionalString(args, "metric") ?? "positiveLogRecords",
            id is null ? null : Guid.Parse(id),
            JsonArgumentParser.ParseDateOnly(args, "baseline_from"), JsonArgumentParser.ParseDateOnly(args, "baseline_to"),
            JsonArgumentParser.GetOptionalString(args, "search"), JsonArgumentParser.GetOptionalString(args, "tag"),
            JsonArgumentParser.GetOptionalBool(args, "is_bad_habit")), ct);
        return result.IsSuccess ? new(true, Payload: result.Value) : ToolResult.FromFailure(result);
    }
}
