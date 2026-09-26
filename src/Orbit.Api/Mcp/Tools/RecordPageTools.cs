using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class RecordPageTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "get_notification_page"), Description("Get the next page of notification records.")]
    public Task<string> GetNotificationPage(ClaimsPrincipal user, [Description("Cursor from a notification record card")] string cursor,
        CancellationToken cancellationToken = default) => GetPage(user, "notifications", cursor, cancellationToken);

    [McpServerTool(Name = "get_tag_page"), Description("Get the next page of tag records.")]
    public Task<string> GetTagPage(ClaimsPrincipal user, [Description("Cursor from a tag record card")] string cursor,
        CancellationToken cancellationToken = default) => GetPage(user, "tags", cursor, cancellationToken);

    [McpServerTool(Name = "get_template_page"), Description("Get the next page of checklist template records.")]
    public Task<string> GetTemplatePage(ClaimsPrincipal user, [Description("Cursor from a template record card")] string cursor,
        CancellationToken cancellationToken = default) => GetPage(user, "templates", cursor, cancellationToken);

    [McpServerTool(Name = "get_api_key_page"), Description("Get the next page of API key metadata. Requires Pro subscription and step-up when enabled.")]
    public async Task<string> GetApiKeyPage(ClaimsPrincipal user,
        [Description("Cursor from an API key record card")] string cursor,
        [Description("Confirmation token from verify_step_up_agent_operation_v2, required only while API-key step-up is switched on")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        if (!RecordListCursor.TryRead(cursor, userId, "keys", out var offset))
            return "Record page not found.";

        var result = await executorBridge.ExecuteAsync(user, "get_api_keys", new { }, confirmationToken, cancellationToken);
        if (!result.Succeeded)
            return result.Message;
        if (result.Payload is not IReadOnlyList<ApiKeyResponse> keys || offset >= keys.Count)
            return "Record page not found.";

        var card = RecordListCardBuilder.BuildKeys(keys, TimeProvider.System.GetUtcNow().UtcDateTime, userId, offset);
        return JsonSerializer.Serialize(card, JsonOptions);
    }

    private async Task<string> GetPage(ClaimsPrincipal user, string kind, string cursor, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetRecordListPageQuery(McpToolHelpers.GetUserId(user), kind, cursor), cancellationToken);
        if (result.IsFailure)
            return $"Error: {result.Error}";

        var card = kind == "notifications"
            ? result.Value with { Items = result.Value.Items.Select(item => item with { Detail = null }).ToList() }
            : result.Value;
        return JsonSerializer.Serialize(card, JsonOptions);
    }
}
