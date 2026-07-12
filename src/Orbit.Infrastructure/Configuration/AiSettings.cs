namespace Orbit.Infrastructure.Configuration;

public sealed class AiSettings
{
    public const string SectionName = "AI";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-4.1-mini";
    public string SubTaskModel { get; init; } = "gpt-5.4-nano";
    public string TranscriptionModel { get; init; } = "whisper-1";
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public int NetworkTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Per-request network timeout for the Batch API file upload/download flow. Batch payloads are
    /// whole JSONL files (far larger than a single chat turn), so they need a longer ceiling than
    /// <see cref="NetworkTimeoutSeconds"/> to avoid spurious timeouts on multi-megabyte transfers.
    /// </summary>
    public int BatchNetworkTimeoutSeconds { get; init; } = 120;
    public int MaxRetries { get; init; } = 2;
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
