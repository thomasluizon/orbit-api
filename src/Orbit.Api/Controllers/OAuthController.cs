using Asp.Versioning;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Api.OAuth;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Api.Controllers;

[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[AllowAnonymous]
#pragma warning disable S6931 // The absolute /.well-known metadata and /oauth authorization routes have protocol-defined paths that cannot share a controller prefix. https://github.com/thomasluizon/orbit-tickets/issues/230
public partial class OAuthController(
    IMediator mediator,
    OAuthAuthorizationStore authStore,
    IGenericRepository<ApiKey> apiKeyRepository,
    IUnitOfWork unitOfWork,
    IOptions<GoogleSettings> googleSettings,
    IConfiguration configuration,
    ILogger<OAuthController> logger) : ControllerBase
{
    private const string InvalidRedirectUriError = "invalid_redirect_uri";
    private const string MissingStateError = "invalid_request";
    private static readonly string[] SupportedResponseTypes = ["code"];
    private static readonly string[] SupportedGrantTypes = ["authorization_code", "refresh_token"];
    private static readonly string[] SupportedCodeChallengeMethods = ["S256"];
    private static readonly string[] SupportedTokenEndpointAuthMethods = ["none"];
    private static readonly string[] SupportedBearerMethods = ["header"];

    private readonly HashSet<string> _allowedRedirectHosts = configuration.GetSection("OAuth:AllowedRedirectHosts")
        .Get<string[]>()?.ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude.ai", "claude.com" };

#pragma warning disable S6932 // GetMetadata reads X-Forwarded-Proto and Request.Host to publish the external authorization-server URLs behind the proxy. https://github.com/thomasluizon/orbit-tickets/issues/230
    [HttpGet("/.well-known/oauth-authorization-server")]
    public IActionResult GetMetadata()
    {
        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        var baseUrl = $"{scheme}://{Request.Host}";
        return Ok(new
        {
            issuer = baseUrl,
            authorization_endpoint = $"{baseUrl}/oauth/authorize",
            token_endpoint = $"{baseUrl}/oauth/token",
            registration_endpoint = $"{baseUrl}/oauth/register",
            response_types_supported = SupportedResponseTypes,
            grant_types_supported = SupportedGrantTypes,
            scopes_supported = AgentScopes.ClaudeDefaultScopes,
            code_challenge_methods_supported = SupportedCodeChallengeMethods,
            token_endpoint_auth_methods_supported = SupportedTokenEndpointAuthMethods
        });
    }
#pragma warning restore S6932

    [HttpPost("/oauth/register")]
    public IActionResult Register([FromBody] JsonElement body)
    {
        var clientName = body.TryGetProperty("client_name", out var name) ? name.GetString() : "MCP Client";

        var requestedUris = Array.Empty<string>();
        if (body.TryGetProperty("redirect_uris", out var uris) && uris.ValueKind == JsonValueKind.Array)
            requestedUris = uris.EnumerateArray().Select(u => u.GetString() ?? string.Empty).ToArray();

        var rejected = requestedUris
            .Where(u => !string.IsNullOrEmpty(u) && !IsRedirectUriAllowed(u))
            .ToArray();
        if (rejected.Length > 0)
        {
            return BadRequest(new
            {
                error = "invalid_redirect_uri",
                error_description = "One or more redirect_uris are not in the allowlist."
            });
        }

        return StatusCode(201, new
        {
            client_id = Guid.NewGuid().ToString(),
            client_name = clientName,
            redirect_uris = requestedUris,
            token_endpoint_auth_method = "none"
        });
    }

#pragma warning disable S6932 // GetProtectedResourceMetadata reads X-Forwarded-Proto and Request.Host to publish the external MCP resource and authorization-server URLs. https://github.com/thomasluizon/orbit-tickets/issues/230
    [HttpGet("/.well-known/oauth-protected-resource")]
    public IActionResult GetProtectedResourceMetadata()
    {
        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        var baseUrl = $"{scheme}://{Request.Host}";
        return Ok(new
        {
            resource = $"{baseUrl}/mcp",
            authorization_servers = new[] { baseUrl },
            bearer_methods_supported = SupportedBearerMethods
        });
    }
#pragma warning restore S6932

    public sealed class AuthorizeRequest
    {
        [FromQuery(Name = "client_id")]
        public required string ClientId { get; init; }

        [FromQuery(Name = "redirect_uri")]
        public required string RedirectUri { get; init; }

        [FromQuery(Name = "response_type")]
        public required string ResponseType { get; init; }

        [FromQuery(Name = "state")]
        public required string State { get; init; }

        [FromQuery(Name = "code_challenge")]
        public required string CodeChallenge { get; init; }

        [FromQuery(Name = "code_challenge_method")]
        public required string CodeChallengeMethod { get; init; }

        [FromQuery(Name = "nonce")]
        public string? Nonce { get; init; }

        [FromQuery(Name = "google_error")]
        public string? GoogleError { get; init; }

        [FromQuery(Name = "scope")]
        public string? Scope { get; init; }
    }

    [HttpGet("/oauth/authorize")]
    [DistributedRateLimit("auth")]
    public IActionResult Authorize([FromQuery] AuthorizeRequest request)
    {
        if (request.ResponseType != "code")
            return BadRequest(new { error = "unsupported_response_type" });

        if (string.IsNullOrEmpty(request.CodeChallenge) || request.CodeChallengeMethod != "S256")
            return BadRequest(new { error = "PKCE with S256 is required" });

        if (!IsRedirectUriAllowed(request.RedirectUri))
            return BadRequest(new { error = InvalidRedirectUriError });

        if (string.IsNullOrEmpty(request.State))
            return BadRequest(new { error = MissingStateError, error_description = "state is required for CSRF protection" });

        if (RequestedScopes(request.Scope) is null)
            return BadRequest(new { error = "invalid_scope" });
        var language = ResolveLanguage();
        var errorMessage = request.GoogleError switch
        {
            "cancelled" => language == "pt-BR"
                ? "O acesso com o Google foi cancelado. Tente novamente ou use seu e-mail."
                : "Google sign-in was cancelled. Try again or use email.",
            "failed" => language == "pt-BR"
                ? "Não foi possível entrar com o Google. Tente novamente ou use seu e-mail."
                : "Google sign-in failed. Try again or use email.",
            "unavailable" => language == "pt-BR"
                ? "O acesso com o Google está indisponível. Use seu e-mail."
                : "Google sign-in is unavailable. Use email instead.",
            _ => null
        };
        var botProtection = configuration.GetSection(BotProtectionSettings.SectionName).Get<BotProtectionSettings>()
            ?? new BotProtectionSettings();
        var html = OAuthLoginPage.Render(
            request.ClientId, request.RedirectUri, request.State,
            request.CodeChallenge, request.CodeChallengeMethod, request.Nonce, errorMessage, language,
            botProtection.Enabled, botProtection.SiteKey, request.Scope,
            configuration.GetSection(AppleSignInSettings.SectionName).Get<AppleSignInSettings>()?.IsMcpConfigured == true);

        return Content(html, "text/html");
    }

    /// <summary>
    /// Serves one self-hosted webfont the authorize page loads. The name is matched against
    /// <see cref="OAuthPageAssets"/>'s closed map, so no path reaches the file system and no third-party
    /// font host appears on a sign-in surface.
    /// </summary>
    [HttpGet("/oauth/assets/{name}")]
    public IActionResult Asset(string name)
    {
        var asset = OAuthPageAssets.TryRead(name);
        if (asset is null)
            return NotFound();

        Response.Headers.CacheControl = $"public, max-age={OAuthPageAssets.CacheSeconds}, immutable";
        return File(asset.Value.Content, asset.Value.ContentType);
    }

    public record SendCodeRequest(string Email, string? Language = "en", string? TurnstileToken = null);

    [HttpPost("/oauth/send-code")]
    [DistributedRateLimit("auth")]
    [RequireBotProtection]
    public async Task<IActionResult> SendCode([FromBody] SendCodeRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new SendCodeCommand(request.Email, request.Language ?? "en"), ct);
        if (result.IsFailure)
            return result.ToErrorResult();

        return Ok(new { success = true });
    }

    public record VerifyCodeRequest(
        string Email, string Code,
        string State, string CodeChallenge, string RedirectUri, string ClientId,
        string? Nonce = null, string? Scope = null);

    [HttpPost("/oauth/verify-code")]
    [DistributedRateLimit("auth")]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeRequest request, CancellationToken ct)
    {
        if (!IsRedirectUriAllowed(request.RedirectUri))
            return BadRequest(new { error = InvalidRedirectUriError });

        if (string.IsNullOrEmpty(request.State))
            return BadRequest(new { error = MissingStateError });

        var result = await mediator.Send(
            new VerifyCodeCommand(request.Email, request.Code), ct);

        if (result.IsFailure)
            return result.ToErrorResult();

        var scopes = RequestedScopes(request.Scope);
        if (scopes is null)
            return BadRequest(new { error = "invalid_scope" });
        var consent = authStore.CreatePendingConsent(new ConsentEntry(result.Value.UserId,
            request.CodeChallenge, request.RedirectUri, request.ClientId, request.State, request.Nonce, ResolveLanguage(), scopes));
        return Ok(new { redirectUrl = $"/oauth/consent?ticket={Uri.EscapeDataString(consent)}" });
    }

    /// <summary>
    /// Starts Google sign-in for an MCP authorization request. The authorize page links here with the same
    /// parameters it was itself given, and this action revalidates every one of them before it allocates any
    /// server state, so a page view alone allocates nothing and cannot grow the pending-request store.
    /// </summary>
    [HttpGet("/oauth/google/start")]
    [DistributedRateLimit("auth")]
    public IActionResult GoogleStart(
        [FromQuery] string client_id,
        [FromQuery] string redirect_uri,
        [FromQuery] string state,
        [FromQuery] string code_challenge,
        [FromQuery] string code_challenge_method,
        [FromQuery] string? nonce = null,
        [FromQuery] string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(client_id)
            || string.IsNullOrEmpty(state)
            || string.IsNullOrEmpty(code_challenge)
            || code_challenge_method != "S256"
            || !IsRedirectUriAllowed(redirect_uri))
            return InvalidGoogleState();

        var requestedScopes = RequestedScopes(scope);
        if (requestedScopes is null)
            return BadRequest(new { error = "invalid_scope" });
        var googleRedirectUri = BuildGoogleRedirectUri();
        if (string.IsNullOrWhiteSpace(googleSettings.Value.ClientId)
            || !googleSettings.Value.AllowedRedirectUris.Contains(googleRedirectUri, StringComparer.Ordinal))
            return RedirectToAuthorize(client_id, redirect_uri, state, code_challenge, nonce, "unavailable", requestedScopes);

        var pending = authStore.TryCreateGoogleRequest(client_id, redirect_uri, state,
            code_challenge, nonce, googleRedirectUri, ResolveLanguage(), requestedScopes);
        if (pending is null)
            return RedirectToAuthorize(client_id, redirect_uri, state, code_challenge, nonce, "unavailable", requestedScopes);

        var url = "https://accounts.google.com/o/oauth2/v2/auth"
            + $"?client_id={Uri.EscapeDataString(googleSettings.Value.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(pending.GoogleRedirectUri)}"
            + "&response_type=code&scope=openid%20email%20profile"
            + $"&state={Uri.EscapeDataString(pending.GoogleState)}"
            + $"&code_challenge={Uri.EscapeDataString(pending.GoogleCodeChallenge)}"
            + "&code_challenge_method=S256&prompt=select_account";
        return Redirect(url);
    }

    [HttpGet("/oauth/google/callback")]
    [DistributedRateLimit("auth")]
    public async Task<IActionResult> GoogleCallback(
        [FromQuery] string? state, [FromQuery] string? code, [FromQuery] string? error, CancellationToken ct)
    {
        var pending = state is null ? null : authStore.ConsumeGoogleRequest(state);
        if (pending is null)
            return InvalidGoogleState();

        if (!IsRedirectUriAllowed(pending.RedirectUri))
            return InvalidGoogleState();

        if (error is not null)
            return RedirectToAuthorize(pending, error == "access_denied" ? "cancelled" : "failed");

        if (string.IsNullOrEmpty(code))
            return RedirectToAuthorize(pending, "failed");

        var result = await mediator.Send(new GoogleCodeAuthCommand(
            code, pending.GoogleCodeVerifier, pending.GoogleRedirectUri, pending.Language,
            PersistGoogleTokens: false), ct);
        if (result.IsFailure)
            return RedirectToAuthorize(pending, "failed");

        var consent = authStore.CreatePendingConsent(new ConsentEntry(result.Value.UserId, pending.CodeChallenge,
            pending.RedirectUri, pending.ClientId, pending.ClientState, pending.Nonce, pending.Language, pending.Scopes));
        return Redirect($"/oauth/consent?ticket={Uri.EscapeDataString(consent)}");
    }

    private IActionResult InvalidGoogleState() => new ContentResult
    {
        Content = OAuthErrorPage.Render(ResolveLanguage()),
        ContentType = "text/html",
        StatusCode = StatusCodes.Status400BadRequest
    };

    private static IActionResult RedirectToAuthorize(GoogleAuthorizationRequest pending, string googleError) =>
        RedirectToAuthorize(pending.ClientId, pending.RedirectUri, pending.ClientState,
            pending.CodeChallenge, pending.Nonce, googleError, pending.Scopes);

    private static IActionResult RedirectToAuthorize(string clientId, string redirectUri, string clientState,
        string codeChallenge, string? nonce, string googleError, IReadOnlyList<string>? scopes = null)
    {
        var url = "/oauth/authorize"
            + $"?client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + "&response_type=code"
            + $"&state={Uri.EscapeDataString(clientState)}"
            + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"
            + "&code_challenge_method=S256"
            + $"&google_error={Uri.EscapeDataString(googleError)}";
        if (nonce is not null)
            url += $"&nonce={Uri.EscapeDataString(nonce)}";
        if (scopes is not null)
            url += $"&scope={Uri.EscapeDataString(string.Join(' ', scopes))}";
        return new RedirectResult(url);
    }

    private string ResolveLanguage() =>
        Request.Headers.AcceptLanguage.ToString().StartsWith("pt", StringComparison.OrdinalIgnoreCase)
            ? "pt-BR" : "en";

#pragma warning disable S6932 // BuildGoogleRedirectUri reads X-Forwarded-Proto and Request.Host for the externally reachable OAuth callback URI. https://github.com/thomasluizon/orbit-tickets/issues/230
    private string BuildGoogleRedirectUri()
    {
        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        return $"{scheme}://{Request.Host}/oauth/google/callback";
    }
#pragma warning restore S6932

    [HttpPost("/oauth/token")]
    [DistributedRateLimit("auth")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token(
        [FromForm] string grant_type,
        [FromForm] string? code,
        [FromForm] string? code_verifier,
        [FromForm] string? redirect_uri,
        CancellationToken ct,
        [FromForm] string? refresh_token = null)
    {
        if (grant_type == "refresh_token")
            return await RefreshAsync(refresh_token, ct);
        if (grant_type != "authorization_code")
            return BadRequest(new { error = "unsupported_grant_type" });

        if (redirect_uri is null || !IsRedirectUriAllowed(redirect_uri))
            return BadRequest(new { error = InvalidRedirectUriError });
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(code_verifier))
            return BadRequest(new { error = "invalid_grant" });
        var entry = authStore.ExchangeCode(code, code_verifier, redirect_uri);
        if (entry is null)
            return BadRequest(new { error = "invalid_grant", error_description = "Invalid, expired, or already used authorization code" });

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<IActionResult>(async token =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"oauth-keys:{entry.UserId}", token);
                var existingKeys = await apiKeyRepository.FindTrackedAsync(
                    k => k.UserId == entry.UserId && k.Name == "Claude.ai", token);
                foreach (var existing in existingKeys)
                    existing.Revoke();

                var familyId = Guid.NewGuid();
                var keyResult = ApiKey.Create(
                    entry.UserId,
                    "Claude.ai",
                    entry.Scopes ?? AgentScopes.ClaudeDefaultScopes,
                    isReadOnly: entry.IsReadOnly,
                    expiresAtUtc: DateTime.UtcNow.AddHours(1),
                    oauthFamilyId: familyId);
                if (keyResult.IsFailure)
                {
                    if (logger.IsEnabled(LogLevel.Error))
                        LogFailedToCreateOAuthApiKey(logger, entry.UserId, keyResult.Error);
                    return StatusCode(500, new { error = "server_error" });
                }

                var (apiKey, rawKey) = keyResult.Value;
                await apiKeyRepository.AddAsync(apiKey, token);
                await unitOfWork.SaveChangesAsync(token);

                if (logger.IsEnabled(LogLevel.Debug))
                    LogOAuthApiKeyCreated(logger, entry.UserId, entry.ClientId);

                var response = new Dictionary<string, object>
                {
                    ["access_token"] = rawKey,
                    ["token_type"] = "Bearer",
                    ["scope"] = string.Join(' ', apiKey.Scopes),
                    ["expires_in"] = 3600,
                    ["refresh_token"] = authStore.CreateRefreshToken(new RefreshEntry(entry.UserId, apiKey.Id, entry.ClientId, familyId))
                };
                if (!string.IsNullOrEmpty(entry.Nonce))
                    response["nonce"] = entry.Nonce;

                return Ok(response);
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            unitOfWork.ResetTracking();
            return BadRequest(new { error = "invalid_grant" });
        }
    }

    [HttpGet("/oauth/consent")]
    public IActionResult Consent([FromQuery] string ticket)
    {
        var consent = authStore.ReadPendingConsent(ticket);
        if (consent is null)
            return InvalidGoogleState();
        return consent.IsConsumed
            ? CompletedConsent(consent.Entry.Language)
            : Content(OAuthConsentPage.Render(ticket, consent.Entry), "text/html");
    }

    [HttpPost("/oauth/consent")]
    [Consumes("application/x-www-form-urlencoded")]
    public IActionResult GrantConsent([FromForm] string ticket, [FromForm] string access)
    {
        if (access is not ("read_only" or "requested"))
            return BadRequest(new { error = "invalid_request" });
        var consent = authStore.ConsumePendingConsent(ticket);
        if (consent is null || !IsRedirectUriAllowed(consent.Entry.RedirectUri))
            return InvalidGoogleState();
        if (consent.IsConsumed)
            return CompletedConsent(consent.Entry.Language);
        var entry = consent.Entry;
        var readOnly = access == "read_only";
        var scopes = entry.Scopes ?? AgentScopes.ClaudeDefaultScopes;
        if (readOnly)
            scopes = scopes.Where(scope => scope.StartsWith("read_", StringComparison.Ordinal) || scope == AgentScopes.CatalogRead).ToArray();
        var code = authStore.CreateCode(entry.UserId, entry.CodeChallenge, entry.RedirectUri,
            entry.ClientId, entry.Nonce, scopes, readOnly);
        return RedirectConsent(entry, "code", code);
    }

    [HttpPost("/oauth/consent/cancel")]
    [Consumes("application/x-www-form-urlencoded")]
    public IActionResult CancelConsent([FromForm] string ticket)
    {
        var consent = authStore.ConsumePendingConsent(ticket);
        if (consent is null || !IsRedirectUriAllowed(consent.Entry.RedirectUri))
            return InvalidGoogleState();
        if (consent.IsConsumed)
            return CompletedConsent(consent.Entry.Language);
        LogOAuthConsentDenied(logger, consent.Entry.ClientId);
        return RedirectConsent(consent.Entry, "error", "access_denied");
    }

    private RedirectResult RedirectConsent(ConsentEntry entry, string key, string value) =>
        Redirect(QueryHelpers.AddQueryString(QueryHelpers.AddQueryString(entry.RedirectUri, key, value), "state", entry.State));

    private static ContentResult CompletedConsent(string language) => new()
    {
        Content = OAuthConsentPage.RenderCompleted(language),
        ContentType = "text/html",
        StatusCode = StatusCodes.Status200OK
    };

    private async Task<IActionResult> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        var grant = string.IsNullOrWhiteSpace(refreshToken) ? null : authStore.ReadRefreshToken(refreshToken);
        if (grant is null)
            return BadRequest(new { error = "invalid_grant" });
        var previous = await apiKeyRepository.GetByIdAsync(grant.ApiKeyId, ct);
        if (previous is null || previous.UserId != grant.UserId)
            return BadRequest(new { error = "invalid_grant" });
        var tracked = await apiKeyRepository.FindOneTrackedAsync(key => key.Id == previous.Id, cancellationToken: ct);
        if (tracked is null)
            return BadRequest(new { error = "invalid_grant" });
        Guid? attemptedKeyId = null;
        var retry = false;
        try
        {
            return await unitOfWork.ExecuteReplayableInTransactionAsync<IActionResult>(async token =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"oauth-keys:{grant.UserId}", token);
                var ownSuccessor = attemptedKeyId is { } successorId
                    ? await apiKeyRepository.FindOneTrackedAsync(key => key.Id == successorId
                        && key.UserId == grant.UserId && key.OAuthFamilyId == grant.FamilyId, cancellationToken: token)
                    : null;
                var current = retry
                    ? ownSuccessor ?? await apiKeyRepository.FindOneTrackedAsync(key => key.Id == previous.Id, cancellationToken: token)
                    : tracked;
                retry = true;
                if (authStore.ConsumeRefreshToken(refreshToken!) is null && ownSuccessor is null)
                {
                    var family = await apiKeyRepository.FindTrackedAsync(
                        key => key.UserId == grant.UserId && !key.IsRevoked
                            && (key.OAuthFamilyId == grant.FamilyId || key.Id == grant.FamilyId), token);
                    foreach (var member in family)
                        member.Revoke();
                    await unitOfWork.SaveChangesAsync(token);
                    return BadRequest(new { error = "invalid_grant" });
                }
                var familyExpiresAtUtc = grant.FamilyExpiresAtUtc ?? previous.CreatedAtUtc.AddDays(30);
                if (current is null || current.IsRevoked || familyExpiresAtUtc <= TimeProvider.System.GetUtcNow().UtcDateTime)
                    return BadRequest(new { error = "invalid_grant" });
                var expiresAtUtc = DateTime.UtcNow.AddHours(1);
                if (familyExpiresAtUtc < expiresAtUtc)
                    expiresAtUtc = familyExpiresAtUtc;
                var created = ApiKey.Create(grant.UserId, "Claude.ai", previous.Scopes, previous.IsReadOnly,
                    expiresAtUtc: expiresAtUtc, oauthFamilyId: grant.FamilyId);
                if (created.IsFailure)
                    return StatusCode(500, new { error = "server_error" });
                current.Revoke();
                var (key, raw) = created.Value;
                attemptedKeyId = key.Id;
                await apiKeyRepository.AddAsync(key, token);
                await unitOfWork.SaveChangesAsync(token);
                return Ok(new
                {
                    access_token = raw,
                    token_type = "Bearer",
                    scope = string.Join(' ', key.Scopes),
                    expires_in = (int)Math.Ceiling((expiresAtUtc - TimeProvider.System.GetUtcNow().UtcDateTime).TotalSeconds),
                    refresh_token = authStore.CreateRefreshToken(new RefreshEntry(grant.UserId, key.Id, grant.ClientId,
                        grant.FamilyId, familyExpiresAtUtc))
                });
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            unitOfWork.ResetTracking();
            return BadRequest(new { error = "invalid_grant" });
        }
    }

    private static IReadOnlyList<string>? RequestedScopes(string? scope)
    {
        var requested = string.IsNullOrWhiteSpace(scope) ? AgentScopes.ClaudeDefaultScopes
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
        return requested.All(item => AgentScopes.ClaudeDefaultScopes.Contains(item, StringComparer.Ordinal)) ? requested : null;
    }

    private bool IsRedirectUriAllowed(string redirectUri)
    {
        return Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirectParsed)
            && redirectParsed.Scheme is "https"
            && _allowedRedirectHosts.Contains(redirectParsed.Host);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to create OAuth API key for user {UserId}: {Error}")]
    private static partial void LogFailedToCreateOAuthApiKey(ILogger logger, Guid userId, string? error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "OAuth API key created for user {UserId} via {ClientId}")]
    private static partial void LogOAuthApiKeyCreated(ILogger logger, Guid userId, string? clientId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "OAuth consent denied for client {ClientId}")]
    private static partial void LogOAuthConsentDenied(ILogger logger, string clientId);
}
