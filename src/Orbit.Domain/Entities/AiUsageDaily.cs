using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>
/// Aggregated AI token usage and computed dollar cost for a single (UTC date, model, purpose) triple.
/// Rows are UPSERTed at the AI completion chokepoint and read once per day by the usage-summary job.
/// </summary>
public class AiUsageDaily : Entity
{
    public DateOnly Date { get; private set; }
    public string Model { get; private set; } = string.Empty;
    public string Purpose { get; private set; } = string.Empty;
    public long Calls { get; private set; }
    public long CachedTokens { get; private set; }
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }
    public long TotalTokens { get; private set; }
    public decimal CostUsd { get; private set; }

    private AiUsageDaily() { }

    public static AiUsageDaily Create(
        DateOnly date,
        string model,
        string purpose,
        long calls,
        long cachedTokens,
        long promptTokens,
        long completionTokens,
        long totalTokens,
        decimal costUsd)
    {
        return new AiUsageDaily
        {
            Date = date,
            Model = model,
            Purpose = purpose,
            Calls = calls,
            CachedTokens = cachedTokens,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            CostUsd = costUsd
        };
    }
}
