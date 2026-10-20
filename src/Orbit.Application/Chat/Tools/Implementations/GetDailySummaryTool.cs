using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetDailySummaryTool(IMediator mediator, IUserDateService userDateService) : IAiTool
{
    public string Name => "get_daily_summary";
    public bool IsReadOnly => true;

    public string Description =>
        "Generate an AI habit summary for today or a date range. Defaults to today.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            date_from = new { type = JsonSchemaTypes.String, description = "Start date (YYYY-MM-DD). Defaults to today." },
            date_to = new { type = JsonSchemaTypes.String, description = "End date (YYYY-MM-DD). Defaults to date_from." },
            language = new { type = JsonSchemaTypes.String, description = "Language code for the summary text. Default: 'en'." }
        },
        required = Array.Empty<string>()
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var dateFrom = JsonArgumentParser.ParseDateOnly(args, "date_from") ?? today;
        var dateTo = JsonArgumentParser.ParseDateOnly(args, "date_to") ?? dateFrom;
        var language = JsonArgumentParser.GetOptionalString(args, "language") ?? "en";

        var result = await mediator.Send(
            new GetDailySummaryQuery(userId, dateFrom, dateTo, language), ct);

        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}
