using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using ModelContextProtocol.Protocol;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class RecordPageTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [McpServerTool(Name = "get_notification_page"), Description("Get the next page of notification records.")]
    public Task<CallToolResult> GetNotificationPage(ClaimsPrincipal user, [Description("Cursor from a notification record card")] string cursor,
        CancellationToken cancellationToken = default) => GetPage(user, "notifications", cursor, cancellationToken);
    [McpServerTool(Name = "get_tag_page"), Description("Get the next page of tag records.")]
    public Task<CallToolResult> GetTagPage(ClaimsPrincipal user, [Description("Cursor from a tag record card")] string cursor,
        CancellationToken cancellationToken = default) => GetPage(user, "tags", cursor, cancellationToken);
    [McpServerTool(Name = "get_template_page"), Description("Get the next page of checklist template records.")]
    public Task<CallToolResult> GetTemplatePage(ClaimsPrincipal user, [Description("Cursor from a template record card")] string cursor,
        CancellationToken cancellationToken = default) => GetPage(user, "templates", cursor, cancellationToken);
    [McpServerTool(Name = "get_api_key_page"), Description("Get the next page of API key metadata. Requires Pro subscription and step-up when enabled.")]
    public async Task<CallToolResult> GetApiKeyPage(ClaimsPrincipal user,
        [Description("Cursor from an API key record card")] string cursor,
        [Description("Confirmation token from verify_step_up_agent_operation_v2, required only while API-key step-up is switched on")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            if (!RecordListCursor.TryRead(cursor, userId, "keys", out var offset))
                return McpToolHelpers.Error("Record page not found.");
            var result = await executorBridge.ExecuteAsync(user, "get_api_keys", new { }, confirmationToken, cancellationToken);
            if (!result.Succeeded)
                return McpToolHelpers.Error(result.Message);
            if (result.Payload is not IReadOnlyList<ApiKeyResponse> keys || offset >= keys.Count)
                return McpToolHelpers.Error("Record page not found.");
            var card = RecordListCardBuilder.BuildKeys(keys, TimeProvider.System.GetUtcNow().UtcDateTime, userId, offset);
            return McpToolHelpers.Text(JsonSerializer.Serialize(card, JsonOptions));
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    private async Task<CallToolResult> GetPage(ClaimsPrincipal user, string kind, string cursor, CancellationToken cancellationToken)
    {
        try
        {
            var result = await mediator.Send(new GetRecordListPageQuery(McpToolHelpers.GetUserId(user), kind, cursor), cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var card = kind == "notifications" ? result.Value with
            {
                Items = result.Value.Items.Select(item => item with { Detail = null }).ToList()
            }

            : result.Value;
            return McpToolHelpers.Text(JsonSerializer.Serialize(card, JsonOptions));
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }
}
