using System.Security.Cryptography;
using System.Text;

namespace Orbit.Api.Controllers;

public partial class OAuthController
{
    private static string StateHash(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    private static string CookieName(string state) => "orbit_oauth_" + StateHash(state);

    private void BindBrowser(string state) => RenewBrowser(state, BrowserSecret(state) ?? authStore.CreateBrowserBinding(state));

    private void RenewBrowser(string state, string secret) => Response.Cookies.Append(CookieName(state), secret, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/oauth",
        MaxAge = TimeSpan.FromMinutes(5),
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
