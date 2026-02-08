using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public class JwtTokenService(IOptions<JwtSettings> options) : ITokenService
{
    private readonly JwtSettings _settings = options.Value;

    public string GenerateToken(Guid userId, string email)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ]),
            Expires = DateTime.UtcNow.AddHours(_settings.ExpiryHours),
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }
}
