using System.Security.Cryptography;
using System.Text;

namespace Orbit.Api.Controllers;

public partial class OAuthController
{
    private static string StateHash(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    private static string CookieName(string state) => "orbit_oauth_" + StateHash(state);

    private void BindBrowser(string state) => Response.Cookies.Append(CookieName(state), StateHash(state), new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/oauth",
        MaxAge = TimeSpan.FromMinutes(5),
        IsEssential = true
    });

    private bool IsBrowserBound(string state)
    {
        var value = Request.Cookies[CookieName(state)];
        return value is { Length: 64 } && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(value), Encoding.ASCII.GetBytes(StateHash(state)));
    }
}
