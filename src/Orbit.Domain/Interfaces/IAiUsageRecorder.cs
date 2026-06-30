namespace Orbit.Domain.Interfaces;

/// <summary>
/// Records one AI completion's token usage and computed dollar cost into the daily usage aggregate.
/// Best-effort: a failure here never fails the AI response it is measuring.
/// </summary>
public interface IAiUsageRecorder
{
    Task RecordAsync(
        string purpose,
        string model,
        long cachedTokens,
        long promptTokens,
        long completionTokens,
        long totalTokens,
        CancellationToken cancellationToken = default);
}
