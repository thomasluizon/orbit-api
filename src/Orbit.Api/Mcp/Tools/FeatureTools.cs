using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;
using Orbit.Application.Chat.FeatureExplanations;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// MCP feature-explanation tools. This is a pure read of user-agnostic embedded content, so it
/// calls <see cref="IFeatureExplanationService"/> directly rather than routing through
/// <see cref="McpExecutorBridge"/> (which exists only for mutation policy + audit coverage).
/// </summary>
[McpServerToolType]
public class FeatureTools(IFeatureExplanationService features)
{
    [McpServerTool(Name = "describe_feature"), Description("Return the authoritative explanation of how an Orbit feature works (streaks, freezes, frequencies, gamification/XP/levels, notifications, free-vs-pro paygate, schedule/overdue math, AI memory). Call before explaining any of these mechanics.")]
    public string DescribeFeature(
        ClaimsPrincipal user,
        [Description("Which Orbit feature to explain: ai-memory, freezes, frequencies, gamification, notifications, paygate, schedule-math, or streaks.")] string featureKey)
    {
        var explanation = features.Get(featureKey);
        if (explanation is null)
            return $"Error: unknown feature '{featureKey}'.";

        return $"# {explanation.DisplayName}\n\n{explanation.Body}";
    }
}
