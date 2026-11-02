using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Tags.Queries;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class TagTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "list_tags"), Description("List all tags for the authenticated user.")]
    public async Task<CallToolResult> ListTags(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var query = new GetTagsQuery(userId);
            var result = await mediator.Send(query, cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var tags = result.Value;
            if (tags.Count == 0)
                return McpToolHelpers.Text("No tags found.");
            var lines = tags.Select(t => $"- {t.Name} (id: {t.Id}, color: {t.Color})");
            return McpToolHelpers.Text($"Tags ({tags.Count}):\n{string.Join("\n", lines)}");
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

    [McpServerTool(Name = "create_tag"), Description("Create a new tag.")]
    public async Task<CallToolResult> CreateTag(
        ClaimsPrincipal user,
        [Description("Tag name")] string name,
        [Description("Hex color code, e.g. #FF5733")] string color,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "create_tag", new { name, color }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Created tag '{name}' (id: {result.TargetId})") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "update_tag"), Description("Update a tag's name and/or color.")]
    public async Task<CallToolResult> UpdateTag(
        ClaimsPrincipal user,
        [Description("The tag ID (GUID)")] string tagId,
        [Description("New tag name")] string name,
        [Description("New hex color code, e.g. #FF5733")] string color,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(tagId, "tagId");
            var result = await executorBridge.ExecuteAsync(user, "update_tag", new { tag_id = tagId, name, color }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Updated tag {tagId}") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "delete_tag"), Description("Delete a tag by ID.")]
    public async Task<CallToolResult> DeleteTag(
        ClaimsPrincipal user,
        [Description("The tag ID (GUID)")] string tagId,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: deleting a tag is destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(tagId, "tagId");
            var result = await executorBridge.ExecuteAsync(user, "delete_tag", new { tag_id = tagId }, confirmationToken, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Deleted tag {tagId}") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "assign_tags"), Description("Assign tags to a habit. Pass the full list of tag IDs (replaces existing tags on the habit).")]
    public async Task<CallToolResult> AssignTags(
        ClaimsPrincipal user,
        [Description("The habit ID (GUID)")] string habitId,
        [Description("Comma-separated tag IDs (GUIDs). Pass empty string to remove all tags.")] string tagIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(habitId, "habitId");
            var ids = string.IsNullOrWhiteSpace(tagIds) ? new List<Guid>() : tagIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => McpInputParser.ParseGuid(s, "tagIds")).ToList();
            var result = await executorBridge.ExecuteAsync(user, "assign_tags", new { habit_id = habitId, tag_ids = ids.Select(i => i.ToString()) }, confirmationToken: null, cancellationToken);
            if (!result.Succeeded)
                return McpToolHelpers.Error(result.Message);
            return ids.Count > 0 ? McpToolHelpers.Text($"Assigned {ids.Count} tags to habit {habitId}") : McpToolHelpers.Text($"Removed all tags from habit {habitId}");
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
