using Orbit.Domain.Models;

namespace Orbit.Application.Common;

public static class AgentPolicyReasons
{
    public const string UnsupportedByPolicy = "unsupported_by_policy";
    public const string StalePreview = "stale_preview";

    /// <summary>
    /// Reports whether the executor turned down an approval because the preview that the person approved is no longer
    /// current. Such an approval executes nothing, so no execution result or plan outcome is recorded for it.
    /// </summary>
    public static bool IsStalePreview(AgentOperationResult operation) =>
        operation is { Status: AgentOperationStatus.Denied, PolicyReason: StalePreview };
}
