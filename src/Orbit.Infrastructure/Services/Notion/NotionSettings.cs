namespace Orbit.Infrastructure.Services.Notion;

public sealed class NotionSettings
{
    public const string SectionName = "Notion";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string[] AllowedRedirectUris { get; set; } = [];
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    public bool Allows(string redirectUri) => Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute)
        && AllowedRedirectUris.Contains(redirectUri, StringComparer.Ordinal);
}
