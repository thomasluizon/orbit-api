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
        "Read a completion summary with totals and an optional narrative recap for a date range, defaulting to today. "
        + "Use for completion totals, a recap or today's next pending step. "
        + "For what to do next today, pass relative_period: today and generate_narrative: true. "
        + "The narrative uses the complete ordered pending schedule and the profile language. Use query_habits for habit listings and schedules; "
        + "a request for habits and goals needs query_habits and query_goals. "
        + "Pass generate_narrative: true to include its saved narrative when available.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            relative_period = RelativeReadPeriod.PeriodSchema(),
            period_count = RelativeReadPeriod.CountSchema(),
            generate_narrative = new { type = JsonSchemaTypes.Boolean, description = "Pass true for a next-step recommendation today or a summary with its narrative. Returns the saved narrative when available, otherwise generates one. Omission defaults to true for multi-day ranges and false for a single day. False reads facts only, without the saved narrative; use false for exact refresh." },
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
            new GetDailySummaryQuery(userId, dateFrom, dateTo, language,
                JsonArgumentParser.GetOptionalBool(args, "generate_narrative") ?? dateFrom != dateTo), ct);

        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}
