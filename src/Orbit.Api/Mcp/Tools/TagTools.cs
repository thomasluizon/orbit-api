using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Tags.Queries;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// MCP tag tools. Mutations route through <see cref="McpExecutorBridge"/> →
/// <see cref="Orbit.Domain.Interfaces.IAgentOperationExecutor"/> with
/// <see cref="Orbit.Domain.Models.AgentExecutionSurface.Mcp"/> for shared policy evaluation and the
/// <c>AgentAuditLogs</c> trail; each forwards a snake_case argument object matching its backing
/// <c>IAiTool</c> schema. <c>assign_tags</c> routes via the chat tool's id path (forwarding
/// <c>tag_ids</c>), preserving the MCP id-based external contract. The <c>list_tags</c> read stays
/// on MediatR.
/// </summary>
[McpServerToolType]
public class TagTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "list_tags"), Description("List all tags for the authenticated user.")]
    public async Task<string> ListTags(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var query = new GetTagsQuery(userId);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var tags = result.Value;
        if (tags.Count == 0)
            return "No tags found.";

        var lines = tags.Select(t => $"- {t.Name} (id: {t.Id}, color: {t.Color})");
        return $"Tags ({tags.Count}):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "create_tag"), Description("Create a new tag.")]
    public async Task<string> CreateTag(
        ClaimsPrincipal user,
        [Description("Tag name")] string name,
        [Description("Hex color code, e.g. #FF5733")] string color,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "create_tag", new
        {
            name,
            color
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Created tag '{name}' (id: {result.TargetId})" : result.Message;
    }

    [McpServerTool(Name = "update_tag"), Description("Update a tag's name and/or color.")]
    public async Task<string> UpdateTag(
        ClaimsPrincipal user,
        [Description("The tag ID (GUID)")] string tagId,
        [Description("New tag name")] string name,
        [Description("New hex color code, e.g. #FF5733")] string color,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "update_tag", new
        {
            tag_id = tagId,
            name,
            color
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Updated tag {tagId}" : result.Message;
    }

    [McpServerTool(Name = "delete_tag"), Description("Delete a tag by ID.")]
    public async Task<string> DeleteTag(
        ClaimsPrincipal user,
        [Description("The tag ID (GUID)")] string tagId,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: deleting a tag is destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "delete_tag", new
        {
            tag_id = tagId
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? $"Deleted tag {tagId}" : result.Message;
    }

    [McpServerTool(Name = "assign_tags"), Description("Assign tags to a habit. Pass the full list of tag IDs (replaces existing tags on the habit).")]
    public async Task<string> AssignTags(
        ClaimsPrincipal user,
        [Description("The habit ID (GUID)")] string habitId,
        [Description("Comma-separated tag IDs (GUIDs). Pass empty string to remove all tags.")] string tagIds,
        CancellationToken cancellationToken = default)
    {
        var ids = string.IsNullOrWhiteSpace(tagIds)
            ? new List<Guid>()
            : tagIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => McpInputParser.ParseGuid(s, "tagIds")).ToList();

        var result = await executorBridge.ExecuteAsync(user, "assign_tags", new
        {
            habit_id = habitId,
            tag_ids = ids.Select(i => i.ToString())
        }, confirmationToken: null, cancellationToken);

        if (!result.Succeeded)
            return result.Message;

        return ids.Count > 0
            ? $"Assigned {ids.Count} tags to habit {habitId}"
            : $"Removed all tags from habit {habitId}";
    }
}
