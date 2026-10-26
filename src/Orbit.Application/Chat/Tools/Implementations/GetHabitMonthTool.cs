using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class GetHabitMonthTool(IMediator mediator) : IAiTool
{
    public string Name => "get_habit_month";
    public bool IsReadOnly => true;
    public string Description => "Read the person's habit calendar day by day using the same schedules and logs as Calendário. "
        + "Use for habit calendar, my month, how did my month go day by day, calendário de hábitos, meu mês, como foi meu mês dia a dia. "
        + "Defaults to the current month in the person's timezone. Pass language from the current message. "
        + "This reads habits, including completed and skipped logs, without Google Calendar events or meetings.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            relative_period = RelativeReadPeriod.PeriodSchema(),
            period_count = RelativeReadPeriod.CountSchema(),
            date_from = new { type = JsonSchemaTypes.String, description = "Inclusive local start date YYYY-MM-DD. Defaults to the first day of the requested or current month." },
            date_to = new { type = JsonSchemaTypes.String, description = "Inclusive local end date YYYY-MM-DD. Defaults to the last day of the start month." },
            language = new { type = JsonSchemaTypes.String, @enum = new[] { "en", "pt", "pt-BR" }, description = "Language of the current message." }
        },
        required = Array.Empty<string>()
    };

    private sealed record Arguments(
        [property: JsonPropertyName("date_from")] DateOnly? From,
        [property: JsonPropertyName("date_to")] DateOnly? To,
        [property: JsonPropertyName("language")] string? Language);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        Arguments? parsed;
        try { parsed = args.Deserialize<Arguments>(); }
        catch (JsonException) { return Invalid(); }
        if (parsed is null) return Invalid();
        var result = await mediator.Send(new GetHabitMonthQuery(userId, parsed.From, parsed.To, parsed.Language ?? "en"), ct);
        return result.IsSuccess ? new(true, Payload: result.Value) : ToolResult.FromFailure(result);
    }

    private static ToolResult Invalid() => new(false, Error: DomainErrors.HabitCalendarRangeInvalid.Message,
        ErrorCode: DomainErrors.HabitCalendarRangeInvalid.Code);
}
