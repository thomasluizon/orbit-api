namespace Orbit.Infrastructure.Configuration;

public sealed class GoogleSettings
{
    public const string SectionName = "Google";
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
}
