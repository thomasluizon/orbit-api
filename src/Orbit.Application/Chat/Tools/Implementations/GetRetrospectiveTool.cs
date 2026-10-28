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
        "Read a retrospective with its narrative, defaulting to week. Returns the saved narrative when available.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            relative_period = RelativeReadPeriod.PeriodSchema(),
            period_count = RelativeReadPeriod.CountSchema(),
            generate_narrative = new { type = JsonSchemaTypes.Boolean, description = "Omit or pass true for a review, retrospective or its narrative. Returns the saved narrative when available, otherwise generates one. Cached narratives are marked stale with refresh recovery because records may have changed. False reads facts only, without the saved narrative; use false for exact refresh." },
            period = new
            {
                type = JsonSchemaTypes.String,
                description = "Time span to look back over. Default: 'week'.",
                @enum = new[] { "week", "month", "quarter", "semester", "year" }
            },
            date_from = new { type = JsonSchemaTypes.String },
            date_to = new { type = JsonSchemaTypes.String },
            language = new { type = JsonSchemaTypes.String, @enum = Orbit.Application.Common.AppConstants.SupportedLanguages, description = "Language code for the retrospective text. Default: 'en'." }
        },
        required = Array.Empty<string>()
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var period = JsonArgumentParser.GetOptionalString(args, "period") ?? "week";
        var language = JsonArgumentParser.GetOptionalString(args, "language") ?? "en";

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(userId, ct);
        var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve(period, today, weekStartDay);

        dateFrom = JsonArgumentParser.ParseDateOnly(args, "date_from") ?? dateFrom;
        dateTo = JsonArgumentParser.ParseDateOnly(args, "date_to") ?? dateTo;
        var result = await mediator.Send(
            new GetRetrospectiveQuery(userId, dateFrom, dateTo, period, language, JsonArgumentParser.GetOptionalBool(args, "generate_narrative") ?? true,
                AllowEmptyPeriod: true), ct);

        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}
