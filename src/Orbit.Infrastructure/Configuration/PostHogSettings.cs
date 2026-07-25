namespace Orbit.Infrastructure.Configuration;

public sealed class PostHogSettings
{
    public const string SectionName = "PostHog";
    public string ApiKey { get; init; } = "";
    public string HostUrl { get; init; } = "https://us.i.posthog.com";
}
