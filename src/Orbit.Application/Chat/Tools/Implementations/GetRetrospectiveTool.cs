using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetRetrospectiveTool(IMediator mediator, IUserDateService userDateService) : IAiTool
{
    public string Name => "get_retrospective";
    public bool IsReadOnly => true;

    public string Description =>
        "Generate the user's AI retrospective over a period (week, month, quarter, semester, or year). Use this when the user asks to look back on their habits over a span of time, e.g. \"how did last month go?\". Default period is week.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            period = new
            {
                type = JsonSchemaTypes.String,
                description = "Time span to look back over. Default: 'week'.",
                @enum = new[] { "week", "month", "quarter", "semester", "year" }
            },
            language = new { type = JsonSchemaTypes.String, description = "Language code for the retrospective text. Default: 'en'." }
        },
        required = Array.Empty<string>()
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var period = JsonArgumentParser.GetOptionalString(args, "period") ?? "week";
        var language = JsonArgumentParser.GetOptionalString(args, "language") ?? "en";

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var dateFrom = today.AddDays(-PeriodToDays(period));
        var dateTo = today;

        var result = await mediator.Send(
            new GetRetrospectiveQuery(userId, dateFrom, dateTo, period, language), ct);

        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }

    private static int PeriodToDays(string period) => period switch
    {
        "week" => 7,
        "month" => 30,
        "quarter" => 90,
        "semester" => 180,
        "year" => 365,
        _ => 7
    };
}
