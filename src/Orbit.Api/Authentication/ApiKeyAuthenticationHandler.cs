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

        if (!IsAllowedApiKeyPath(Request.Path))
            return AuthenticateResult.Fail("API keys are only allowed for agent endpoints.");

        var rawKey = authHeader["Bearer ".Length..].Trim();
        if (rawKey.Length < 12)
            return AuthenticateResult.Fail("Invalid API key format.");

        var keyPrefix = rawKey[..12];

        using var scope = serviceProvider.CreateScope();
        var apiKeyRepository = scope.ServiceProvider.GetRequiredService<IGenericRepository<ApiKey>>();
        var payGate = scope.ServiceProvider.GetRequiredService<IPayGateService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await apiKeyRepository.FindTrackedAsync(
            k => k.KeyPrefix == keyPrefix && !k.IsRevoked);

#pragma warning disable S3267 // Loops should be simplified with LINQ -- BCrypt.Verify is expensive, early exit via foreach is intentional
        foreach (var candidate in candidates)
#pragma warning restore S3267
        {
            if (BCrypt.Net.BCrypt.Verify(rawKey, candidate.KeyHash))
            {
                if (candidate.HasExpired())
                    return AuthenticateResult.Fail("API key expired.");

                var gateCheck = await payGate.CanReadApiKeys(candidate.UserId, Context.RequestAborted);
                if (gateCheck.IsFailure)
                    return AuthenticateResult.Fail("API keys are not available for this plan.");

                candidate.MarkUsed();
                await unitOfWork.SaveChangesAsync();

                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, candidate.UserId.ToString()),
                    new("auth_method", "api_key"),
                    new("api_key_id", candidate.Id.ToString()),
                    new("api_key_read_only", candidate.IsReadOnly.ToString())
                };

                claims.AddRange(candidate.Scopes.Select(scope => new Claim("scope", scope)));
                var identity = new ClaimsIdentity(claims, Scheme.Name);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, Scheme.Name);
                return AuthenticateResult.Success(ticket);
            }
        }

        return AuthenticateResult.Fail("Invalid API key.");
    }

    private static bool IsAllowedApiKeyPath(PathString path)
    {
        return path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/chat", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/ai", StringComparison.OrdinalIgnoreCase);
    }
}
