using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class GetHabitRankingTool(IMediator mediator) : IAiTool
{
    public string Name => "get_habit_ranking";
    public bool IsReadOnly => true;
    public string Description => "Read the full positive recurring-habit ranking by completion log count, including children. "
        + "Use this for most completed habits or ranking de hábitos, never retrospective consistency highlights. "
        + "Resolve the requested window first; ask for dates if unspecified. Last six months or últimos 6 meses means "
        + "rolling_months with months=6, not 180 days. Explicit complete calendar months use complete_months.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            date_from = new { type = JsonSchemaTypes.String, description = "Inclusive local start date YYYY-MM-DD. Use with date_to_exclusive, or use window and months." },
            date_to_exclusive = new { type = JsonSchemaTypes.String, description = "Exclusive local end date YYYY-MM-DD; an inclusive requested end must advance by one day." },
            window = new { type = JsonSchemaTypes.String, @enum = new[] { "rolling_months", "complete_months" } },
            months = new { type = JsonSchemaTypes.Integer, minimum = 1, maximum = 120 },
            metric = new { type = JsonSchemaTypes.String, @enum = new[] { HabitRankingDefinition.CompletionLogRecords } },
            habit_ids = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String }, sensitive = true },
            page_size = new { type = JsonSchemaTypes.Integer, minimum = 1, maximum = HabitRankingDefinition.MaxPageSize },
            query_token = new { type = JsonSchemaTypes.String, sensitive = true, description = "Reuse for paging or refresh with no date, metric, filter or page-size overrides." },
            cursor = new { type = JsonSchemaTypes.String, sensitive = true },
            refresh = new { type = JsonSchemaTypes.Boolean, description = "Recalculate the bound absolute query." }
        },
        required = Array.Empty<string>()
    };

    public object GetResultSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            queryToken = new { type = JsonSchemaTypes.String, sensitive = true },
            nextCursor = new { type = JsonSchemaTypes.String, sensitive = true },
            query = new { type = JsonSchemaTypes.Object, properties = new { habitIds = new { type = JsonSchemaTypes.Array, sensitive = true } } },
            rows = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.Object, properties = new { name = new { type = JsonSchemaTypes.String, sensitive = true } } } }
        }
    };

    private sealed record Arguments(
        [property: JsonPropertyName("date_from")] DateOnly? DateFrom,
        [property: JsonPropertyName("date_to_exclusive")] DateOnly? DateToExclusive,
        [property: JsonPropertyName("window")] string? Window,
        [property: JsonPropertyName("months")] int? Months,
        [property: JsonPropertyName("metric")] string? Metric,
        [property: JsonPropertyName("habit_ids")] Guid[]? HabitIds,
        [property: JsonPropertyName("page_size")] int? PageSize,
        [property: JsonPropertyName("query_token")] string? QueryToken,
        [property: JsonPropertyName("cursor")] string? Cursor,
        [property: JsonPropertyName("refresh")] bool? Refresh = null);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        Arguments? parsed;
        try
        {
            parsed = args.Deserialize<Arguments>();
        }
        catch (JsonException)
        {
            return new ToolResult(false, Error: "Invalid habit ranking arguments.", ErrorCode: "HABIT_RANKING_INVALID");
        }
        if (parsed is null)
            return new ToolResult(false, Error: "Habit ranking arguments are required.", ErrorCode: "HABIT_RANKING_INVALID");
        var result = await mediator.Send(new GetHabitRankingQuery(userId, parsed.DateFrom, parsed.DateToExclusive,
            parsed.Window, parsed.Months, parsed.Metric, parsed.HabitIds, parsed.PageSize,
            parsed.QueryToken, parsed.Cursor, parsed.Refresh == true), ct);
        return result.IsSuccess ? new ToolResult(true, Payload: result.Value) : ToolResult.FromFailure(result);
    }
}
