using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class TagTools(IMediator mediator)
{
    [McpServerTool(Name = "list_tags"), Description("List all tags for the authenticated user.")]
    public async Task<string> ListTags(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
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
        var userId = GetUserId(user);
        var command = new CreateTagCommand(userId, name, color);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? $"Created tag '{name}' (id: {result.Value})"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "update_tag"), Description("Update a tag's name and/or color.")]
    public async Task<string> UpdateTag(
        ClaimsPrincipal user,
        [Description("The tag ID (GUID)")] string tagId,
        [Description("New tag name")] string name,
        [Description("New hex color code, e.g. #FF5733")] string color,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new UpdateTagCommand(userId, Guid.Parse(tagId), name, color);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Updated tag {tagId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "delete_tag"), Description("Delete a tag by ID.")]
    public async Task<string> DeleteTag(
        ClaimsPrincipal user,
        [Description("The tag ID (GUID)")] string tagId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new DeleteTagCommand(userId, Guid.Parse(tagId));
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Deleted tag {tagId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "assign_tags"), Description("Assign tags to a habit. Pass the full list of tag IDs (replaces existing tags on the habit).")]
    public async Task<string> AssignTags(
        ClaimsPrincipal user,
        [Description("The habit ID (GUID)")] string habitId,
        [Description("Comma-separated tag IDs (GUIDs). Pass empty string to remove all tags.")] string tagIds,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var ids = string.IsNullOrWhiteSpace(tagIds)
            ? new List<Guid>()
            : tagIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Guid.Parse).ToList();

        var command = new AssignTagsCommand(userId, Guid.Parse(habitId), ids);
        var result = await mediator.Send(command, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: {result.Error}";

        return ids.Count > 0
            ? $"Assigned {ids.Count} tags to habit {habitId}"
            : $"Removed all tags from habit {habitId}";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        return Guid.Parse(claim);
    }
}
