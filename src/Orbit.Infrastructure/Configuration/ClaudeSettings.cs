namespace Orbit.Infrastructure.Configuration;

public sealed class ClaudeSettings
{
    public const string SectionName = "Claude";
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
}
