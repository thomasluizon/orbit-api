using System.Security.Cryptography;
using System.Text;
using Orbit.Api.OAuth;

namespace Orbit.Api.Controllers;

public partial class OAuthController
{
    private const string BrowserCookiePrefix = "orbit_oauth_";
    private const string BrowserCookiePath = "/oauth";

    private static string StateHash(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    private static string CookieName(string state) => BrowserCookiePrefix + StateHash(state);

    internal static bool IsBrowserBindingCookie(string name, CookieOptions options) =>
        options.Path == BrowserCookiePath && name.Length == BrowserCookiePrefix.Length + 64
        && name.StartsWith(BrowserCookiePrefix, StringComparison.Ordinal)
        && name.Skip(BrowserCookiePrefix.Length).All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private void BindBrowser(string state) => RenewBrowser(state, BrowserSecret(state) ?? authStore.CreateBrowserBinding(state));

    private void RenewBrowser(string state, string secret) => Response.Cookies.Append(CookieName(state), secret, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = BrowserCookiePath,
        MaxAge = OAuthAuthorizationStore.BrowserBindingLifetime,
        IsEssential = true
    });

    private string? BrowserSecret(string state)
    {
        var secret = Request.Cookies[CookieName(state)];
        return secret is { Length: 43 } && authStore.ReadBrowserBinding(secret) == state ? secret : null;
    }

    private bool IsBrowserBound(string state, string? bindingHash)
    {
        var secret = Request.Cookies[CookieName(state)];
        return secret is { Length: 43 } && bindingHash is { Length: 64 }
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(StateHash(secret)), Encoding.ASCII.GetBytes(bindingHash));
    }
}
