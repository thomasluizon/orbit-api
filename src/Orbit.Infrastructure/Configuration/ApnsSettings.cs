namespace Orbit.Infrastructure.Configuration;

public sealed class ApnsSettings
{
    public const string SectionName = "Apns";
    public string KeyId { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string Topic { get; set; } = "";
    public bool UseSandbox { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(TeamId)
        && !string.IsNullOrWhiteSpace(PrivateKey) && !string.IsNullOrWhiteSpace(Topic);
}
