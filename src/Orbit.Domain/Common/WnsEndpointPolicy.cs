namespace Orbit.Domain.Common;

public static class WnsEndpointPolicy
{
    /// <summary>
    /// https://learn.microsoft.com/en-us/windows/apps/develop/notifications/push-notifications/wns-overview
    /// </summary>
    public static bool IsAllowed(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443
        && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0
        && (uri.Host.Equals("notify.windows.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase));
}
