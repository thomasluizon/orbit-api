using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Api.OAuth;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Api.Controllers;

[ApiController]
[AllowAnonymous]
#pragma warning disable S6931 // OAuth/MCP well-known URLs follow protocol-mandated paths with no common prefix
#pragma warning disable S107 // OAuth controller legitimately requires many DI dependencies
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
    private static readonly string[] SupportedGrantTypes = ["authorization_code"];
    private static readonly string[] SupportedCodeChallengeMethods = ["S256"];
    private static readonly string[] SupportedTokenEndpointAuthMethods = ["none"];
    private static readonly string[] SupportedBearerMethods = ["header"];

    private readonly HashSet<string> _allowedRedirectHosts = configuration.GetSection("OAuth:AllowedRedirectHosts")
        .Get<string[]>()?.ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude.ai", "claude.com" };

#pragma warning disable S6932 // Raw Request.Headers needed for reverse proxy X-Forwarded-Proto detection
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

#pragma warning disable S6932 // Raw Request.Headers needed for reverse proxy X-Forwarded-Proto detection
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

    [HttpGet("/oauth/authorize")]
    public IActionResult Authorize(
        [FromQuery] string client_id,
        [FromQuery] string redirect_uri,
        [FromQuery] string response_type,
        [FromQuery] string state,
        [FromQuery] string code_challenge,
        [FromQuery] string code_challenge_method,
        [FromQuery] string? nonce = null,
        [FromQuery] string? google_error = null)
    {
        if (response_type != "code")
            return BadRequest(new { error = "unsupported_response_type" });

        if (string.IsNullOrEmpty(code_challenge) || code_challenge_method != "S256")
            return BadRequest(new { error = "PKCE with S256 is required" });

        if (!IsRedirectUriAllowed(redirect_uri))
            return BadRequest(new { error = InvalidRedirectUriError });

        if (string.IsNullOrEmpty(state))
            return BadRequest(new { error = MissingStateError, error_description = "state is required for CSRF protection" });

        var language = Request.Headers.AcceptLanguage.ToString().StartsWith("pt", StringComparison.OrdinalIgnoreCase)
            ? "pt-BR" : "en";
        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        var googleRedirectUri = $"{scheme}://{Request.Host}/oauth/google/callback";
        var googleState = authStore.CreateGoogleRequest(client_id, redirect_uri, state,
            code_challenge, nonce, googleRedirectUri, language);
        var errorMessage = google_error switch
        {
            "cancelled" => language == "pt-BR"
                ? "O acesso com Google foi cancelado. Tente novamente ou use seu email."
                : "Google sign-in was cancelled. Try again or use email.",
            "failed" => language == "pt-BR"
                ? "Não foi possível entrar com Google. Tente novamente ou use seu email."
                : "Google sign-in failed. Try again or use email.",
            "unavailable" => language == "pt-BR"
                ? "O acesso com Google está indisponível. Use seu email."
                : "Google sign-in is unavailable. Use email instead.",
            _ => null
        };
        var html = OAuthLoginPage.Render(
            client_id, redirect_uri, state,
            code_challenge, code_challenge_method, googleState, nonce, errorMessage, language);

        return Content(html, "text/html");
    }

    public record SendCodeRequest(string Email, string? Language = "en");

    [HttpPost("/oauth/send-code")]
    [DistributedRateLimit("auth")]
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
        string? Nonce = null);

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

        var loginResponse = result.Value;
        var authCode = authStore.CreateCode(
            loginResponse.UserId, request.CodeChallenge, request.RedirectUri, request.ClientId, request.Nonce);

        var separator = request.RedirectUri.Contains('?') ? "&" : "?";
        var redirectUrl = $"{request.RedirectUri}{separator}code={Uri.EscapeDataString(authCode)}&state={Uri.EscapeDataString(request.State)}";

        return Ok(new { redirectUrl });
    }

    [HttpGet("/oauth/google/start")]
    [DistributedRateLimit("auth")]
    public IActionResult GoogleStart([FromQuery] string state)
    {
        var pending = authStore.GetGoogleRequest(state);
        if (pending is null)
            return InvalidGoogleState();

        if (string.IsNullOrWhiteSpace(googleSettings.Value.ClientId)
            || !googleSettings.Value.AllowedRedirectUris.Contains(pending.GoogleRedirectUri, StringComparer.Ordinal))
            return RedirectToAuthorize(pending, "unavailable");

        var url = "https://accounts.google.com/o/oauth2/v2/auth"
            + $"?client_id={Uri.EscapeDataString(googleSettings.Value.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(pending.GoogleRedirectUri)}"
            + "&response_type=code&scope=openid%20email%20profile"
            + $"&state={Uri.EscapeDataString(state)}"
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

        if (error is not null)
            return RedirectToAuthorize(pending, error == "access_denied" ? "cancelled" : "failed");

        if (string.IsNullOrEmpty(code))
            return RedirectToAuthorize(pending, "failed");

        var result = await mediator.Send(new GoogleCodeAuthCommand(
            code, pending.GoogleCodeVerifier, pending.GoogleRedirectUri, pending.Language), ct);
        if (result.IsFailure)
            return RedirectToAuthorize(pending, "failed");

        var authCode = authStore.CreateCode(result.Value.UserId, pending.CodeChallenge,
            pending.RedirectUri, pending.ClientId, pending.Nonce);
        var separator = pending.RedirectUri.Contains('?') ? "&" : "?";
        return Redirect($"{pending.RedirectUri}{separator}code={Uri.EscapeDataString(authCode)}&state={Uri.EscapeDataString(pending.ClientState)}");
    }

    private static IActionResult InvalidGoogleState() => new ContentResult
    {
        Content = "<html><body><p>This authorization request is invalid or expired. Return to your MCP client and try again.</p></body></html>",
        ContentType = "text/html",
        StatusCode = StatusCodes.Status400BadRequest
    };

    private static IActionResult RedirectToAuthorize(GoogleAuthorizationRequest pending, string googleError)
    {
        var url = "/oauth/authorize"
            + $"?client_id={Uri.EscapeDataString(pending.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(pending.RedirectUri)}"
            + "&response_type=code"
            + $"&state={Uri.EscapeDataString(pending.ClientState)}"
            + $"&code_challenge={Uri.EscapeDataString(pending.CodeChallenge)}"
            + "&code_challenge_method=S256"
            + $"&google_error={Uri.EscapeDataString(googleError)}";
        if (pending.Nonce is not null)
            url += $"&nonce={Uri.EscapeDataString(pending.Nonce)}";
        return new RedirectResult(url);
    }

    [HttpPost("/oauth/token")]
    [DistributedRateLimit("auth")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token(
        [FromForm] string grant_type,
        [FromForm] string code,
        [FromForm] string code_verifier,
        [FromForm] string redirect_uri,
        CancellationToken ct)
    {
        if (grant_type != "authorization_code")
            return BadRequest(new { error = "unsupported_grant_type" });

        if (!IsRedirectUriAllowed(redirect_uri))
            return BadRequest(new { error = InvalidRedirectUriError });

        var entry = authStore.ExchangeCode(code, code_verifier, redirect_uri);
        if (entry is null)
            return BadRequest(new { error = "invalid_grant", error_description = "Invalid, expired, or already used authorization code" });

        var existingKeys = await apiKeyRepository.FindTrackedAsync(
            k => k.UserId == entry.UserId && k.Name == "Claude.ai", ct);
        foreach (var existing in existingKeys)
            existing.Revoke();

        var keyResult = ApiKey.Create(
            entry.UserId,
            "Claude.ai",
            AgentScopes.ClaudeDefaultScopes,
            isReadOnly: false);
        if (keyResult.IsFailure)
        {
            if (logger.IsEnabled(LogLevel.Error))
                LogFailedToCreateOAuthApiKey(logger, entry.UserId, keyResult.Error);
            return StatusCode(500, new { error = "server_error" });
        }

        var (apiKey, rawKey) = keyResult.Value;
        await apiKeyRepository.AddAsync(apiKey, ct);
        await unitOfWork.SaveChangesAsync(ct);

        if (logger.IsEnabled(LogLevel.Debug))
            LogOAuthApiKeyCreated(logger, entry.UserId, entry.ClientId);

        var response = new Dictionary<string, object>
        {
            ["access_token"] = rawKey,
            ["token_type"] = "Bearer",
            ["scope"] = string.Join(' ', AgentScopes.ClaudeDefaultScopes)
        };
        if (!string.IsNullOrEmpty(entry.Nonce))
            response["nonce"] = entry.Nonce;

        return Ok(response);
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
}
