using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Events;

public sealed class EventTicketService(IOptions<JwtSettings> options)
{
    private readonly JwtSettings _settings = options.Value;

    public (string Ticket, DateTime ExpiresAtUtc) Create(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        var sessionId = principal.FindFirstValue("orbit_session_id")
            ?? throw new UnauthorizedAccessException("Session ID not found in token");
        var accessExpiry = principal.FindFirstValue(JwtRegisteredClaimNames.Exp)
            ?? throw new UnauthorizedAccessException("Token expiry not found");
        if (!long.TryParse(accessExpiry, CultureInfo.InvariantCulture, out var expirySeconds))
            throw new UnauthorizedAccessException("Token expiry is invalid");

        var expiresAtUtc = DateTime.UtcNow.AddSeconds(60);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("orbit_session_id", sessionId),
                new Claim("orbit_access_exp", expirySeconds.ToString(CultureInfo.InvariantCulture)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            ]),
            Expires = expiresAtUtc,
            Issuer = _settings.Issuer,
            Audience = AudienceFor(_settings),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey)),
                SecurityAlgorithms.HmacSha256)
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresAtUtc);
    }

    public static string AudienceFor(JwtSettings settings) => $"{settings.Audience}:events";

    public static DateTimeOffset GetStreamExpiry(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue("orbit_access_exp")
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Exp)
            ?? throw new UnauthorizedAccessException("Token expiry not found");
        return DateTimeOffset.FromUnixTimeSeconds(long.Parse(value, CultureInfo.InvariantCulture));
    }
}
