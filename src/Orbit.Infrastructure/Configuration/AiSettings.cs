namespace Orbit.Infrastructure.Configuration;

public sealed class AiSettings
{
    public const string SectionName = "AI";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-6-luna";
    public string SubTaskModel { get; init; } = "gpt-6-luna";
    public string TranscriptionModel { get; init; } = "whisper-1";
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public int NetworkTimeoutSeconds { get; init; } = 15;
    public int BufferedNetworkTimeoutSeconds { get; init; } = 90;
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// Bounds buffered calls independently of per-attempt timeouts and retry count.
    /// Allows a 90-second failed attempt, a 90-second successful retry and five seconds for
    /// retry wait and processing. Longer server-directed retry waits consume this same deadline.
    /// </summary>
    public int CompletionTimeoutSeconds { get; init; } = 185;
    public TimeSpan CompletionTimeout => TimeSpan.FromSeconds(CompletionTimeoutSeconds);
    public TimeSpan StreamingCompletionTimeout => TimeSpan.FromSeconds((long)NetworkTimeoutSeconds * (2L * MaxRetries + 1));
    public Dictionary<string, AiModelPrice> Pricing { get; init; } = new();
}

/// <summary>
/// Per-model OpenAI prices in USD per 1,000,000 tokens, used to convert token counts into dollar cost.
/// </summary>
public sealed class AiModelPrice
{
    public decimal InputPerMillionUsd { get; init; }
    public decimal CachedInputPerMillionUsd { get; init; }
    public decimal OutputPerMillionUsd { get; init; }
}
