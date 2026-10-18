namespace Orbit.Api.RateLimiting;

public sealed class BffForwardingSettings
{
    public const string SectionName = "Auth";

    public string BffForwardingSecret { get; set; } = "";
}
