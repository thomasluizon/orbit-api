namespace Orbit.Infrastructure.Configuration;

public sealed class WnsSettings
{
    public const string SectionName = "Wns";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    public bool IsConfigured => Guid.TryParse(TenantId, out var tenantId) && tenantId != Guid.Empty
        && Guid.TryParse(ClientId, out var clientId) && clientId != Guid.Empty
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
