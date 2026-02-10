namespace Orbit.Infrastructure.Configuration;

public sealed class GeminiSettings
{
    public const string SectionName = "Gemini";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gemini-2.5-flash-lite";
    public string BaseUrl { get; init; } = "https://generativelanguage.googleapis.com/v1beta";
}
