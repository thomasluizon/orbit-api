namespace Orbit.Application.Common;

public sealed class AppleSignInSettings
{
    public const string SectionName = "AppleSignIn";
    public string KeyId { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string NativeClientId { get; set; } = "";
    public string WebClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string McpRedirectUri { get; set; } = "";
    public string WebOrigin { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(TeamId)
        && !string.IsNullOrWhiteSpace(PrivateKey) && !string.IsNullOrWhiteSpace(NativeClientId)
        && !string.IsNullOrWhiteSpace(WebClientId) && IsHttpsUrl(RedirectUri) && IsHttpsUrl(WebOrigin)
        && new Uri(WebOrigin).AbsolutePath == "/" && string.IsNullOrEmpty(new Uri(WebOrigin).Query);
    public bool IsMcpConfigured => IsConfigured && IsHttpsUrl(McpRedirectUri);

    private static bool IsHttpsUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo)
        && Uri.CheckHostName(uri.Host) == UriHostNameType.Dns && uri.Host != "localhost";
}
