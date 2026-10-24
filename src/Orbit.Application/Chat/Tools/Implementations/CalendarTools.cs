using System.Text.Json;
using MediatR;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using System.Globalization;
using Orbit.Application.Common;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public record CalendarOverviewPayload(
    IReadOnlyList<CalendarEventItem> Events,
    CalendarAutoSyncStateResponse? AutoSyncState,
    IReadOnlyList<CalendarSyncSuggestionItem> Suggestions,
    IReadOnlyList<UserCalendarItem>? Calendars = null, string? Recovery = null,
    IReadOnlyList<string>? SelectedCalendarIds = null,
    string? SelectionMode = null);

public class GetCalendarOverviewTool(IMediator mediator) : IAiTool
{
    public string Name => "get_calendar_overview";
    public string Description => "Read calendar events, selected calendars, auto-sync state, and sync suggestions in one payload.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            as_of = new { type = JsonSchemaTypes.String },
            calendar_ids = new { type = new[] { JsonSchemaTypes.Array, "null" }, items = new { type = JsonSchemaTypes.String } },
            date_from = new { type = JsonSchemaTypes.String },
            date_to = new { type = JsonSchemaTypes.String },
            window_from_utc = new { type = JsonSchemaTypes.String },
            window_to_utc = new { type = JsonSchemaTypes.String },
            include_calendars = new { type = new[] { JsonSchemaTypes.Boolean, "null" } },
            include_events = new { type = new[] { JsonSchemaTypes.Boolean, "null" } },
            include_auto_sync_state = new { type = new[] { JsonSchemaTypes.Boolean, "null" } },
            include_suggestions = new { type = new[] { JsonSchemaTypes.Boolean, "null" } }
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var includeCalendars = JsonArgumentParser.GetOptionalBool(args, "include_calendars") ?? true;
        var includeEvents = JsonArgumentParser.GetOptionalBool(args, "include_events") ?? true;
        var includeAutoSyncState = JsonArgumentParser.GetOptionalBool(args, "include_auto_sync_state") ?? true;
        var includeSuggestions = JsonArgumentParser.GetOptionalBool(args, "include_suggestions") ?? true;

        var window = ParseWindow(args);
        var hasSelection = args.TryGetProperty("calendar_ids", out var selection);
        var selectedIds = selection.ValueKind == JsonValueKind.Array
            ? selection.EnumerateArray().Select(id => id.GetString()!).ToList() : null;
        var events = includeEvents
            ? await mediator.Send(new GetCalendarEventsQuery(userId, Window: window, CalendarIds: selectedIds, PersistConnectionState: false, UseCurrentSelection: !hasSelection), ct)
            : Result.Success(new List<CalendarEventItem>());
        string? recovery = null;
        if (events.IsFailure)
        {
            if (events.ErrorCode != ErrorMessages.CalendarNotConnected.Code && events.ErrorCode != ErrorMessages.CalendarReconnectRequired.Code)
                return ToolResult.FromFailure(events);
            recovery = "reconnect";
            events = Result.Success(new List<CalendarEventItem>());
        }

        CalendarAutoSyncStateResponse? autoSyncState = null;
        if (includeAutoSyncState || includeCalendars)
        {
            var autoSyncStateResult = await mediator.Send(new GetCalendarAutoSyncStateQuery(userId), ct);
            if (autoSyncStateResult.IsFailure && autoSyncStateResult.ErrorCode != "PAY_GATE")
                return ToolResult.FromFailure(autoSyncStateResult);
            if (autoSyncStateResult.IsSuccess)
                autoSyncState = autoSyncStateResult.Value;
        }

        var suggestions = includeSuggestions
            ? await mediator.Send(new GetCalendarSyncSuggestionsQuery(userId,
                JsonArgumentParser.ParseDateOnly(args, "date_from") ?? JsonArgumentParser.ParseDateOnly(args, "as_of"),
                JsonArgumentParser.ParseDateOnly(args, "date_to"), selectedIds, UseCurrentSelection: !hasSelection), ct)
            : Result.Success(new List<CalendarSyncSuggestionItem>());
        if (suggestions.IsFailure && suggestions.ErrorCode != Result.PayGateErrorCode)
            return ToolResult.FromFailure(suggestions);

        var calendars = includeCalendars
            ? await mediator.Send(new GetUserCalendarsQuery(userId, PersistConnectionState: false), ct)
            : Result.Success(new List<UserCalendarItem>());
        if (CannotRecoverCalendars(calendars, recovery))
            return ToolResult.FromFailure(calendars);

        return new ToolResult(true, Payload: AddSelection(new CalendarOverviewPayload(
            events.Value, autoSyncState,
            suggestions.IsSuccess ? suggestions.Value : [],
            includeCalendars && calendars.IsSuccess ? calendars.Value : null, recovery), includeAutoSyncState));
    }
    private static bool CannotRecoverCalendars(Result<List<UserCalendarItem>> calendars, string? recovery) =>
        calendars.IsFailure && calendars.ErrorCode != Result.PayGateErrorCode && recovery is null;

    private static CalendarOverviewPayload AddSelection(CalendarOverviewPayload payload, bool includeAutoSyncState) => payload with
    {
        SelectedCalendarIds = payload.AutoSyncState?.SelectedCalendarIds,
        SelectionMode = payload.AutoSyncState?.SelectionMode,
        AutoSyncState = includeAutoSyncState ? payload.AutoSyncState : null
    };

    private static CalendarReadWindow? ParseWindow(JsonElement args) =>
        DateTime.TryParse(JsonArgumentParser.GetOptionalString(args, "window_from_utc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var fromAtUtc)
        && DateTime.TryParse(JsonArgumentParser.GetOptionalString(args, "window_to_utc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var toAtUtc)
            ? new(fromAtUtc, toAtUtc) : null;

}

public class ManageCalendarSyncTool(IMediator mediator) : IPreparedIntentTool
{
    public string Name => "manage_calendar_sync";
    public string Description => "Select calendars (an empty list resets to all owned calendars), enable or disable calendar auto-sync, dismiss imports or suggestions, or trigger a sync run.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new
            {
                type = JsonSchemaTypes.String,
                @enum = new[] { "set_selected_calendars", "set_auto_sync", "dismiss_import", "dismiss_suggestion", "run_sync" }
            },
            calendar_ids = new { type = new[] { JsonSchemaTypes.Array, "null" }, items = new { type = JsonSchemaTypes.String } },
            enabled = new { type = new[] { JsonSchemaTypes.Boolean, "null" } },
            suggestion_id = new { type = new[] { JsonSchemaTypes.String, "null" } }
        },
        required = new[] { "action" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    public Task<PreparedToolIntent?> PrepareIntentAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (action == "run_sync" || action == "set_selected_calendars"
            && (!args.TryGetProperty("calendar_ids", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0))
            return Task.FromResult<PreparedToolIntent?>(null);
        return ChatToolArgumentCheck.PrepareAsync(this, args,
            () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));
    }

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "set_selected_calendars" => await SetSelectedCalendarsAsync(args, userId, ct, checkOnly),
            "set_auto_sync" => await SetAutoSyncAsync(args, userId, ct, checkOnly),
            "dismiss_import" => await ChatToolMediator.RunAsync(mediator, new DismissCalendarImportCommand(userId), userId, "Dismissed calendar import prompt", new { action }, ct, checkOnly),
            "dismiss_suggestion" => await DismissSuggestionAsync(args, userId, ct, checkOnly),
            "run_sync" => await RunSyncAsync(userId, ct, checkOnly),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> SetSelectedCalendarsAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (!args.TryGetProperty("calendar_ids", out var ids) || ids.ValueKind != JsonValueKind.Array
            || ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())))
            return new ToolResult(false, Error: "calendar_ids is required; use an empty list for all owned calendars.");
        var calendarIds = ids.EnumerateArray().Select(id => id.GetString()!).ToList();
        return await ChatToolMediator.RunAsync(mediator,
            new SetSelectedCalendarsCommand(userId, PreparedToolIntent.Values(calendarIds)), userId,
            "Calendar selection updated", new { action = "set_selected_calendars", calendar_ids = calendarIds }, ct, checkOnly);
    }

    private async Task<ToolResult> SetAutoSyncAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var enabled = JsonArgumentParser.GetOptionalBool(args, "enabled");
        if (!enabled.HasValue)
            return new ToolResult(false, Error: "enabled is required.");

        return await ChatToolMediator.RunAsync(
            mediator,
            new SetCalendarAutoSyncCommand(userId, enabled.Value),
            userId,
            enabled.Value ? "Calendar auto-sync enabled" : "Calendar auto-sync disabled",
            new { action = "set_auto_sync", enabled },
            ct, checkOnly);
    }

    private async Task<ToolResult> DismissSuggestionAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var suggestionId = JsonArgumentParser.GetOptionalString(args, "suggestion_id");
        if (!Guid.TryParse(suggestionId, out var parsedId))
            return new ToolResult(false, Error: "suggestion_id must be a valid GUID.");

        return await ChatToolMediator.RunAsync(
            mediator,
            new DismissCalendarSuggestionCommand(userId, parsedId),
            parsedId,
            "Dismissed calendar sync suggestion",
            new { action = "dismiss_suggestion", suggestionId },
            ct, checkOnly);
    }

    private async Task<ToolResult> RunSyncAsync(Guid userId, CancellationToken ct, bool checkOnly)
    {
        var command = new RunCalendarAutoSyncCommand(userId, IsOpportunistic: false);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Calendar sync requested", Payload: result.Value)
            : ToolResult.FromFailure(result, userId.ToString());
    }
}
