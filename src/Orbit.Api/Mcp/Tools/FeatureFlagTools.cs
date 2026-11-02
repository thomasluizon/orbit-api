using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Api.Extensions;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Queries;
using ModelContextProtocol.Protocol;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class FeatureFlagTools(IMediator mediator, McpExecutorBridge executorBridge, IServiceProvider services)
{
    [McpServerTool(Name = FeatureFlagActions.ToolName), Description("Manage feature flags. Actions: list, create, enable_for_accounts, disable_for_accounts, enable_for_everyone, disable_for_everyone. Administrator only.")]
    public async Task<CallToolResult> ManageFeatureFlags(
        ClaimsPrincipal user,
        [Description("Action to perform")] string action,
        [Description("Feature flag key")] string? key = null,
        [Description("Account email addresses for account targeting")] string[]? emails = null,
        [Description("One-line description for a new flag")] string? description = null,
        string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await FeatureFlagToolAccess.IsAdminAsync(user, services))
                throw FeatureFlagToolAccess.NotFound();
            if (action == FeatureFlagActions.List)
            {
                var result = await mediator.Send(new ListFeatureFlagsQuery(user.GetUserId()), cancellationToken);
                await services.GetRequiredService<IAgentAuditService>().RecordAsync(new AgentAuditEntry(
                    user.GetUserId(), FeatureFlagActions.CapabilityId, FeatureFlagActions.ToolName,
                    AgentExecutionSurface.Mcp, user.GetAgentAuthMethod(), AgentRiskClass.Low,
                    result.IsSuccess ? AgentPolicyDecisionStatus.Allowed : AgentPolicyDecisionStatus.Denied,
                    result.IsSuccess ? AgentOperationStatus.Succeeded : AgentOperationStatus.Denied,
                    RedactedArguments: "{\"action\":\"list\"}", Error: result.IsFailure ? result.Error : null), cancellationToken);
                return result.IsSuccess ? McpToolHelpers.Text(JsonSerializer.Serialize(result.Value)) : McpToolHelpers.Error($"Error: {result.Error}");
            }

            var outcome = await executorBridge.ExecuteAsync(user, FeatureFlagActions.ToolName, new { action, key, emails, description }, confirmationToken, cancellationToken);
            return outcome.Succeeded ? McpToolHelpers.Text(JsonSerializer.Serialize(outcome.Payload)) : McpToolHelpers.Error(outcome.Message);
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
