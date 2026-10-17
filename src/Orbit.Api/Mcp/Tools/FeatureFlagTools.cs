using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Api.Extensions;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class FeatureFlagTools(IMediator mediator, McpExecutorBridge executorBridge, IServiceProvider services)
{
    [McpServerTool(Name = FeatureFlagActions.ToolName), Description("Manage feature flags. Actions: list, create, enable_for_accounts, disable_for_accounts, enable_for_everyone, disable_for_everyone. Administrator only.")]
    public async Task<string> ManageFeatureFlags(
        ClaimsPrincipal user,
        [Description("Action to perform")] string action,
        [Description("Feature flag key")] string? key = null,
        [Description("Account email addresses for account targeting")] string[]? emails = null,
        [Description("One-line description for a new flag")] string? description = null,
        string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!await FeatureFlagToolAccess.IsAdminAsync(user, services))
            throw FeatureFlagToolAccess.NotFound();

        if (action == FeatureFlagActions.List)
        {
            var result = await mediator.Send(new ListFeatureFlagsQuery(user.GetUserId()), cancellationToken);
            return result.IsSuccess ? JsonSerializer.Serialize(result.Value) : $"Error: {result.Error}";
        }

        var outcome = await executorBridge.ExecuteAsync(user, FeatureFlagActions.ToolName,
            new { action, key, emails, description }, confirmationToken, cancellationToken);
        return outcome.Succeeded ? JsonSerializer.Serialize(outcome.Payload) : outcome.Message;
    }
}
