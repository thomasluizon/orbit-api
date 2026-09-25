namespace Orbit.Application.Common;

public sealed class BotProtectionSettings
{
    public const string SectionName = "BotProtection";

    public bool Enabled { get; set; }
    public string SecretKey { get; set; } = "";
}
