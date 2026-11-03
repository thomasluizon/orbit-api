using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;
using Orbit.Application.Chat.FeatureExplanations;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Orbit.Api.Mcp.Tools;
/// <summary>
/// MCP feature-explanation tools. This is a pure read of user-agnostic embedded content, so it
/// calls <see cref = "IFeatureExplanationService"/> directly rather than routing through
/// <see cref = "McpExecutorBridge"/> (which exists only for mutation policy + audit coverage).
/// </summary>
[McpServerToolType]
public class FeatureTools(IFeatureExplanationService features)
{
    [McpServerTool(Name = "describe_feature"), Description("Return the authoritative explanation of how an Orbit feature works (streaks, freezes, frequencies, gamification/XP/levels, notifications, free-vs-pro paygate, schedule/overdue math). Call before explaining any of these mechanics.")]
    public CallToolResult DescribeFeature(
        ClaimsPrincipal user,
        [Description("Which Orbit feature to explain: freezes, frequencies, gamification, notifications, paygate, schedule-math, or streaks.")] string featureKey)
    {
        var explanation = features.Get(featureKey);
        if (explanation is null)
            return McpToolHelpers.Error($"Error: unknown feature '{featureKey}'.");
        return McpToolHelpers.Text($"# {explanation.DisplayName}\n\n{explanation.Body}");
    }
}
