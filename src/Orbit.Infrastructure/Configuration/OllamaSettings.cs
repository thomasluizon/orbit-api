namespace Orbit.Infrastructure.Configuration;

public sealed class OllamaSettings
{
    public const string SectionName = "Ollama";
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "phi3.5:3.8b";
}
