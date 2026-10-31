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
        + "For a current-day summary, use get_daily_summary with relative_period: today. Reserve this tool for the requested habit-calendar window. "
        + "A calendar window is required: month for a month view, or date_range for an explicit range longer than one day. Never use this tool for a single-day request. Pass language from the current message. "
        + "This reads habits, including completed and skipped logs, without Google Calendar events or meetings. Organizing an unstructured message into a task and habit preview needs query_habits for reuse, not a habit month read. "
        + "This tool reads habit logs and does not report goal progress; use query_goals for goal progress.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            window = new { type = JsonSchemaTypes.String, @enum = new[] { "month", "date_range" }, description = "Required habit-calendar window. month defaults to the current local month; date_range requires date_from and date_to spanning more than one day. A day summary uses get_daily_summary." },
            relative_period = new { type = JsonSchemaTypes.String, @enum = new[] { "this_month", "last_month" }, description = "Use only for a relative month request. The server resolves the person's local dates." },
            date_from = new { type = JsonSchemaTypes.String, description = "Inclusive local start date YYYY-MM-DD. Defaults to the first day of the requested or current month." },
            date_to = new { type = JsonSchemaTypes.String, description = "Inclusive local end date YYYY-MM-DD. Defaults to the last day of the start month." },
            language = new { type = JsonSchemaTypes.String, @enum = new[] { "en", "pt", "pt-BR" }, description = "Language of the current message." }
        },
        required = new[] { "window" }
    };

    private sealed record Arguments(
        [property: JsonPropertyName("window")] string? Window,
        [property: JsonPropertyName("date_from")] DateOnly? From,
        [property: JsonPropertyName("date_to")] DateOnly? To,
        [property: JsonPropertyName("language")] string? Language);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        Arguments? parsed;
        try { parsed = args.Deserialize<Arguments>(); }
        catch (JsonException) { return Invalid(); }
        if (parsed is null || parsed.Window is not ("month" or "date_range")
            || parsed.Window == "date_range" && (parsed.From is null || parsed.To is null)
            || parsed.Window == "date_range" && parsed.From is { } from && parsed.To is { } to && to <= from
            || args.TryGetProperty("relative_period", out var relative) && relative.ValueKind != JsonValueKind.Null
                && (relative.ValueKind != JsonValueKind.String || relative.GetString() is not ("this_month" or "last_month"))) return Invalid();
        var result = await mediator.Send(new GetHabitMonthQuery(userId, parsed.From, parsed.To, parsed.Language ?? "en"), ct);
        return result.IsSuccess ? new(true, Payload: result.Value) : ToolResult.FromFailure(result);
    }

    private static ToolResult Invalid() => new(false, Error: DomainErrors.HabitCalendarRangeInvalid.Message,
        ErrorCode: DomainErrors.HabitCalendarRangeInvalid.Code);
}
