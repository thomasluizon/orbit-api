namespace Orbit.Infrastructure.Configuration;

public sealed class DiscordAlertSettings
{
    public const string SectionName = "DiscordAlerts";
    public string WebhookUrl { get; init; } = "";
}
