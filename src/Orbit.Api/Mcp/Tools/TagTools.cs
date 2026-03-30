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

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        return Guid.Parse(claim);
    }
}
