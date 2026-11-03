using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Orbit.Infrastructure.Services;
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
        var store = scope.ServiceProvider.GetRequiredService<IApiKeyAuthenticationStore>();
        var usageTracker = scope.ServiceProvider.GetRequiredService<IApiKeyUsageTracker>();
        var cache = scope.ServiceProvider.GetRequiredService<AuthValidationCache>();
        var cacheKey = "verified-api-key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
        if (!cache.TryGet<ApiKeyAuthenticationCandidate>(cacheKey, out var verified))
        {
            var epoch = cache.Epoch;
            verified = await store.FindAsync(keyPrefix, Context.RequestAborted);
            if (verified is null || verified.Key.IsRevoked)
                return AuthenticateResult.Fail("Invalid API key.");
            if (verified.Key.HasExpired())
                return AuthenticateResult.Fail("API key expired.");
            if (verified.OwnerIsDeactivated)
                return AuthenticateResult.Fail("Account is deactivated.");
            if (!verified.HasProAccess)
                return AuthenticateResult.Fail("API keys are not available for this plan.");
            if (!verified.Key.Verify(rawKey))
                return AuthenticateResult.Fail("Invalid API key.");
            var expiryAtUtc = verified.Key.ExpiresAtUtc;
            if (verified.EntitlementEndsAtUtc is { } entitlement && (expiryAtUtc is null || entitlement < expiryAtUtc))
                expiryAtUtc = entitlement;
            cache.Set(cacheKey, verified.Key.UserId, verified, epoch, session: false, expiryAtUtc);
        }
        var candidate = verified!.Key;
        if (candidate.LastUsedAtUtc is null || candidate.LastUsedAtUtc <= TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(-1))
        {
            await usageTracker.MarkUsedAsync(candidate.Id, Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, candidate.UserId.ToString()),
            new("auth_method", candidate.OAuthFamilyId.HasValue ? "oauth" : "api_key"),
            new("api_key_id", candidate.Id.ToString()),
            new("api_key_read_only", candidate.IsReadOnly.ToString())
        };

        claims.AddRange(candidate.Scopes.Select(scope => new Claim("scope", scope)));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    private static bool IsAllowedApiKeyPath(PathString path)
    {
        return path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/chat", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/ai", StringComparison.OrdinalIgnoreCase);
    }
}
