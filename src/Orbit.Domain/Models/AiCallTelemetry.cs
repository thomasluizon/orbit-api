namespace Orbit.Domain.Models;

public sealed record AiCallTelemetry(
    string TraceId,
    double LatencySeconds,
    bool IsError,
    IReadOnlyList<string> ToolNames,
    string? Plan = null,
    bool HasUsage = true);

public sealed class AiTurnTelemetry(string? traceId = null, string? plan = null)
{
    public string TraceId { get; } = string.IsNullOrWhiteSpace(traceId) ? Guid.NewGuid().ToString("N") : traceId;
    public string? Plan { get; } = plan;
    public int RoundCount { get; private set; }
    public int ToolCallCount { get; private set; }

    public void RecordRound(int toolCallCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(toolCallCount);
        RoundCount++;
        ToolCallCount += toolCallCount;
    }
}
