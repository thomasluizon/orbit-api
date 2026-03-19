namespace Orbit.Infrastructure.Configuration;

public class VapidSettings
{
    public const string SectionName = "Vapid";
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string Subject { get; set; } = "";
}
