using System.Text.Json;
using MediatR;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public record CalendarOverviewPayload(
    IReadOnlyList<CalendarEventItem> Events,
    CalendarAutoSyncStateResponse? AutoSyncState,
    IReadOnlyList<CalendarSyncSuggestionItem> Suggestions);

public class GetCalendarOverviewTool(IMediator mediator) : IAiTool
{
    public string Name => "get_calendar_overview";
    public string Description => "Read calendar events, auto-sync state, and sync suggestions in one payload.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            include_events = new { type = JsonSchemaTypes.Boolean },
            include_auto_sync_state = new { type = JsonSchemaTypes.Boolean },
            include_suggestions = new { type = JsonSchemaTypes.Boolean }
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var includeEvents = JsonArgumentParser.GetOptionalBool(args, "include_events") ?? true;
        var includeAutoSyncState = JsonArgumentParser.GetOptionalBool(args, "include_auto_sync_state") ?? true;
        var includeSuggestions = JsonArgumentParser.GetOptionalBool(args, "include_suggestions") ?? true;

        var events = includeEvents
            ? await mediator.Send(new GetCalendarEventsQuery(userId), ct)
            : Result.Success(new List<CalendarEventItem>());
        if (events.IsFailure)
            return ToolResult.FromFailure(events);

        CalendarAutoSyncStateResponse? autoSyncState = null;
        if (includeAutoSyncState)
        {
            var autoSyncStateResult = await mediator.Send(new GetCalendarAutoSyncStateQuery(userId), ct);
            if (autoSyncStateResult.IsFailure && autoSyncStateResult.ErrorCode != "PAY_GATE")
                return ToolResult.FromFailure(autoSyncStateResult);
            if (autoSyncStateResult.IsSuccess)
                autoSyncState = autoSyncStateResult.Value;
        }

        var suggestions = includeSuggestions
            ? await mediator.Send(new GetCalendarSyncSuggestionsQuery(userId), ct)
            : Result.Success(new List<CalendarSyncSuggestionItem>());
        if (suggestions.IsFailure && suggestions.ErrorCode != Result.PayGateErrorCode)
            return ToolResult.FromFailure(suggestions);

        return new ToolResult(true, Payload: new CalendarOverviewPayload(
            events.Value, autoSyncState,
            suggestions.IsSuccess ? suggestions.Value : []));
    }
}

public class ManageCalendarSyncTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "manage_calendar_sync";
    public string Description => "Enable or disable calendar auto-sync, dismiss imports or suggestions, or trigger a sync run.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new
            {
                type = JsonSchemaTypes.String,
                @enum = new[] { "set_auto_sync", "dismiss_import", "dismiss_suggestion", "run_sync" }
            },
            enabled = new { type = JsonSchemaTypes.Boolean, nullable = true },
            suggestion_id = new { type = JsonSchemaTypes.String, nullable = true }
        },
        required = new[] { "action" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "set_auto_sync" => await SetAutoSyncAsync(args, userId, ct, checkOnly),
            "dismiss_import" => await ChatToolMediator.RunAsync(mediator, new DismissCalendarImportCommand(userId), userId, "Dismissed calendar import prompt", new { action }, ct, checkOnly),
            "dismiss_suggestion" => await DismissSuggestionAsync(args, userId, ct, checkOnly),
            "run_sync" => await RunSyncAsync(userId, ct, checkOnly),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
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
