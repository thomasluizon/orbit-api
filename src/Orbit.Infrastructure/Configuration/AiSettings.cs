namespace Orbit.Infrastructure.Configuration;

public sealed class AiSettings
{
    public const string SectionName = "AI";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-4.1-mini";
    public string TranscriptionModel { get; init; } = "whisper-1";
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public int NetworkTimeoutSeconds { get; init; } = 15;
    public int MaxRetries { get; init; } = 2;
}
