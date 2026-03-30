using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Authentication;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider serviceProvider) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer orb_", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Not an API key token.");

        var rawKey = authHeader["Bearer ".Length..].Trim();
        if (rawKey.Length < 12)
            return AuthenticateResult.Fail("Invalid API key format.");

        var keyPrefix = rawKey[..12];

        using var scope = serviceProvider.CreateScope();
        var apiKeyRepository = scope.ServiceProvider.GetRequiredService<IGenericRepository<ApiKey>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await apiKeyRepository.FindTrackedAsync(
            k => k.KeyPrefix == keyPrefix && !k.IsRevoked);

        foreach (var candidate in candidates)
        {
            if (BCrypt.Net.BCrypt.Verify(rawKey, candidate.KeyHash))
            {
                candidate.MarkUsed();
                await unitOfWork.SaveChangesAsync();

                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, candidate.UserId.ToString()),
                    new Claim("auth_method", "api_key")
                };
                var identity = new ClaimsIdentity(claims, Scheme.Name);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, Scheme.Name);
                return AuthenticateResult.Success(ticket);
            }
        }

        return AuthenticateResult.Fail("Invalid API key.");
    }
}
