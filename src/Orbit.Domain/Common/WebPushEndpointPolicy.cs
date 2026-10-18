namespace Orbit.Domain.Common;

public static class WebPushEndpointPolicy
{
    /// <summary>
    /// Google: https://web.dev/articles/codelab-notifications-push-server
    /// Mozilla: https://mozilla-services.github.io/autopush-rs/http.html
    /// Apple: https://developer.apple.com/documentation/usernotifications/sending-web-push-notifications-in-web-apps-and-browsers
    /// WNS: https://learn.microsoft.com/en-us/windows/apps/develop/notifications/push-notifications/firewall-allowlist-config
    /// </summary>
    public static bool IsAllowed(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0)
            return false;

        return uri.Host.Equals("fcm.googleapis.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("updates.push.services.mozilla.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".push.apple.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase);
    }
}
