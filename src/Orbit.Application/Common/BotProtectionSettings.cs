namespace Orbit.Application.Common;

public sealed class BotProtectionSettings
{
    public const string SectionName = "BotProtection";

    public string[] AllowedHostnames { get; set; } = [];

    public bool AllowsHostname(string? hostname) => hostname is not null
        && AllowedHostnames.Contains(hostname, StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; }
    public string SiteKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
}
