using System.Net.Http.Headers;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Api.OAuth;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Api.Controllers;

[ApiController]
public class OAuthController(
    IMediator mediator,
    OAuthAuthorizationStore authStore,
    IGenericRepository<ApiKey> apiKeyRepository,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IHttpClientFactory httpClientFactory,
    IOptions<GoogleSettings> googleSettings,
    ILogger<OAuthController> logger) : ControllerBase
{
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
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" }
        });
    }

    [HttpGet("/.well-known/oauth-protected-resource")]
    public IActionResult GetProtectedResourceMetadata()
    {
        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        var baseUrl = $"{scheme}://{Request.Host}";
        return Ok(new
        {
            resource = $"{baseUrl}/mcp",
            authorization_servers = new[] { baseUrl },
            bearer_methods_supported = new[] { "header" }
        });
    }

    [HttpGet("/oauth/authorize")]
    public IActionResult Authorize(
        [FromQuery] string client_id,
        [FromQuery] string redirect_uri,
        [FromQuery] string response_type,
        [FromQuery] string state,
        [FromQuery] string code_challenge,
        [FromQuery] string code_challenge_method)
    {
        if (response_type != "code")
            return BadRequest(new { error = "unsupported_response_type" });

        if (string.IsNullOrEmpty(code_challenge) || code_challenge_method != "S256")
            return BadRequest(new { error = "PKCE with S256 is required" });

        var googleClientId = googleSettings.Value.ClientId ?? "";
        var html = OAuthLoginPage.Render(
            client_id, redirect_uri, state,
            code_challenge, code_challenge_method, googleClientId);

        return Content(html, "text/html");
    }

    public record SendCodeRequest(string Email, string? Language = "en");

    [HttpPost("/oauth/send-code")]
    public async Task<IActionResult> SendCode([FromBody] SendCodeRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new SendCodeCommand(request.Email, request.Language ?? "en"), ct);
        if (result.IsFailure)
            return BadRequest(new { error = result.Error });

        return Ok(new { success = true });
    }

    public record VerifyCodeRequest(
        string Email, string Code,
        string State, string CodeChallenge, string RedirectUri, string ClientId);

    [HttpPost("/oauth/verify-code")]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(
            new VerifyCodeCommand(request.Email, request.Code), ct);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error });

        var loginResponse = result.Value;
        var authCode = authStore.CreateCode(
            loginResponse.UserId, request.CodeChallenge, request.RedirectUri, request.ClientId);

        var separator = request.RedirectUri.Contains('?') ? "&" : "?";
        var redirectUrl = $"{request.RedirectUri}{separator}code={Uri.EscapeDataString(authCode)}&state={Uri.EscapeDataString(request.State)}";

        return Ok(new { redirectUrl });
    }

    public record GoogleAuthRequest(
        string Credential,
        string State, string CodeChallenge, string RedirectUri, string ClientId);

    [HttpPost("/oauth/google")]
    public async Task<IActionResult> GoogleAuth([FromBody] GoogleAuthRequest request, CancellationToken ct)
    {
        // Validate Google credential via Supabase (same pattern as GoogleAuthCommand)
        var client = httpClientFactory.CreateClient("Supabase");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/v1/user");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Credential);

        var response = await client.SendAsync(httpRequest, ct);
        if (!response.IsSuccessStatusCode)
            return BadRequest(new { error = "Invalid or expired Google sign-in token" });

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
        if (string.IsNullOrEmpty(email))
            return BadRequest(new { error = "Could not retrieve email from Google account" });

        // Find or create user
        var user = await userRepository.FindOneTrackedAsync(u => u.Email == email, cancellationToken: ct);
        if (user is null)
        {
            var name = "User";
            if (root.TryGetProperty("user_metadata", out var metadata))
            {
                if (metadata.TryGetProperty("full_name", out var fullName) && fullName.GetString() is string fn)
                    name = fn;
                else if (metadata.TryGetProperty("name", out var nameProperty) && nameProperty.GetString() is string n)
                    name = n;
            }

            var createResult = Domain.Entities.User.Create(name, email);
            if (createResult.IsFailure)
                return BadRequest(new { error = createResult.Error });

            user = createResult.Value;
            await userRepository.AddAsync(user, ct);
            await unitOfWork.SaveChangesAsync(ct);
        }

        if (user.IsDeactivated)
        {
            user.CancelDeactivation();
            await unitOfWork.SaveChangesAsync(ct);
        }

        var authCode = authStore.CreateCode(
            user.Id, request.CodeChallenge, request.RedirectUri, request.ClientId);

        var separator = request.RedirectUri.Contains('?') ? "&" : "?";
        var redirectUrl = $"{request.RedirectUri}{separator}code={Uri.EscapeDataString(authCode)}&state={Uri.EscapeDataString(request.State)}";

        return Ok(new { redirectUrl });
    }

    [HttpPost("/oauth/token")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token(
        [FromForm] string grant_type,
        [FromForm] string code,
        [FromForm] string code_verifier,
        [FromForm] string? client_id,
        [FromForm] string redirect_uri,
        CancellationToken ct)
    {
        if (grant_type != "authorization_code")
            return BadRequest(new { error = "unsupported_grant_type" });

        var entry = authStore.ExchangeCode(code, code_verifier, redirect_uri);
        if (entry is null)
            return BadRequest(new { error = "invalid_grant", error_description = "Invalid, expired, or already used authorization code" });

        // Create an API key for this user (name: "Claude.ai", no Pro gate)
        var keyResult = ApiKey.Create(entry.UserId, "Claude.ai");
        if (keyResult.IsFailure)
        {
            logger.LogError("Failed to create OAuth API key for user {UserId}: {Error}", entry.UserId, keyResult.Error);
            return StatusCode(500, new { error = "server_error" });
        }

        var (apiKey, rawKey) = keyResult.Value;
        await apiKeyRepository.AddAsync(apiKey, ct);
        await unitOfWork.SaveChangesAsync(ct);

        logger.LogInformation("OAuth API key created for user {UserId} via {ClientId}", entry.UserId, entry.ClientId);

        return Ok(new
        {
            access_token = rawKey,
            token_type = "Bearer",
            scope = "all"
        });
    }
}
