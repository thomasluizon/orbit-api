using System.Text.Json;
using MediatR;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

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
            return new ToolResult(false, Error: events.Error);

        CalendarAutoSyncStateResponse? autoSyncState = null;
        if (includeAutoSyncState)
        {
            var autoSyncStateResult = await mediator.Send(new GetCalendarAutoSyncStateQuery(userId), ct);
            if (autoSyncStateResult.IsFailure)
                return new ToolResult(false, Error: autoSyncStateResult.Error);

            autoSyncState = autoSyncStateResult.Value;
        }

        var suggestions = includeSuggestions
            ? await mediator.Send(new GetCalendarSyncSuggestionsQuery(userId), ct)
            : Result.Success(new List<CalendarSyncSuggestionItem>());
        if (suggestions.IsFailure)
            return new ToolResult(false, Error: suggestions.Error);

        return new ToolResult(true, Payload: new
        {
            events = events.Value,
            autoSyncState,
            suggestions = suggestions.Value
        });
    }
}

public class ManageCalendarSyncTool(IMediator mediator) : IAiTool
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

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "set_auto_sync" => await SetAutoSyncAsync(args, userId, ct),
            "dismiss_import" => await ExecuteAsync(new DismissCalendarImportCommand(userId), userId, "Dismissed calendar import prompt", new { action }, ct),
            "dismiss_suggestion" => await DismissSuggestionAsync(args, userId, ct),
            "run_sync" => await RunSyncAsync(userId, ct),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> SetAutoSyncAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var enabled = JsonArgumentParser.GetOptionalBool(args, "enabled");
        if (!enabled.HasValue)
            return new ToolResult(false, Error: "enabled is required.");

        return await ExecuteAsync(
            new SetCalendarAutoSyncCommand(userId, enabled.Value),
            userId,
            enabled.Value ? "Calendar auto-sync enabled" : "Calendar auto-sync disabled",
            new { action = "set_auto_sync", enabled },
            ct);
    }

    private async Task<ToolResult> DismissSuggestionAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var suggestionId = JsonArgumentParser.GetOptionalString(args, "suggestion_id");
        if (!Guid.TryParse(suggestionId, out var parsedId))
            return new ToolResult(false, Error: "suggestion_id must be a valid GUID.");

        return await ExecuteAsync(
            new DismissCalendarSuggestionCommand(userId, parsedId),
            parsedId,
            "Dismissed calendar sync suggestion",
            new { action = "dismiss_suggestion", suggestionId },
            ct);
    }

    private async Task<ToolResult> RunSyncAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new RunCalendarAutoSyncCommand(userId, IsOpportunistic: false), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Calendar sync requested", Payload: result.Value)
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }

    private async Task<ToolResult> ExecuteAsync(IRequest<Result> command, Guid entityId, string entityName, object payload, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: entityId.ToString(), EntityName: entityName, Payload: payload)
            : new ToolResult(false, EntityId: entityId.ToString(), Error: result.Error);
    }
}
