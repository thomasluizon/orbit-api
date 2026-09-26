using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.UserFacts.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class UserFactTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "get_user_facts"), Description("Get all AI-learned facts about the user.")]
    public async Task<string> GetUserFacts(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var query = new GetUserFactsQuery(userId);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var facts = result.Value;
        if (facts.Count == 0)
            return "No user facts stored.";

        var lines = facts.Select(f =>
            $"- {f.FactText}" +
            (f.Category is not null ? $" [{f.Category}]" : "") +
            $" (id: {f.Id})");

        return $"User Facts ({facts.Count}):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "delete_user_fact"), Description("Delete a specific AI-learned fact about the user.")]
    public async Task<string> DeleteUserFact(
        ClaimsPrincipal user,
        [Description("The user fact ID (GUID)")] string factId,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: deleting a fact is destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "delete_user_facts", new
        {
            fact_id = factId
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? $"Deleted user fact {factId}" : result.Message;
    }
}
