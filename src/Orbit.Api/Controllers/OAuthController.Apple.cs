using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.OAuth;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Services;

namespace Orbit.Api.Controllers;

[Route("oauth/apple")]
public partial class OAuthController
{
    [HttpGet("start")]
    [DistributedRateLimit("auth")]
    public async Task<IActionResult> AppleStart([FromServices] AppleAuthorizationFlow flow,
        [FromQuery] string client_id, [FromQuery] string redirect_uri, [FromQuery] string state,
        [FromQuery] string code_challenge, [FromQuery] string code_challenge_method,
        [FromQuery] string? nonce = null, [FromQuery] string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(client_id) || client_id.Length > 1024 || string.IsNullOrEmpty(state) || state.Length > 2048
            || string.IsNullOrEmpty(code_challenge) || code_challenge.Length > 128 || code_challenge_method != "S256"
            || !IsRedirectUriAllowed(redirect_uri))
            return BadRequest();
        var scopes = RequestedScopes(scope);
        if (scopes is null)
            return BadRequest(new { error = "invalid_scope" });
        var client = await ValidateClientAsync(client_id, redirect_uri, HttpContext.RequestAborted);
        if (client.Error is not null)
            return BadRequest(new { error = client.Error });
        var language = ResolveLanguage();
        var context = new ConsentEntry(Guid.Empty, code_challenge, redirect_uri, client_id, state, nonce, language, scopes,
            client.ClientName);
        var url = flow.Start(state, language, null, JsonSerializer.Serialize(context));
        return url is null ? StatusCode(StatusCodes.Status503ServiceUnavailable) : Redirect(url);
    }

    [HttpPost("callback")]
    [DistributedRateLimit("auth")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> AppleCallback([FromServices] AppleAuthorizationFlow flow,
        [FromForm] AuthController.AppleCallbackRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (request.Error is not null || string.IsNullOrEmpty(request.Code) || request.State.Length > 256
            || request.Code.Length > 4096 || request.User?.Length > 16_384)
            return Unauthorized();
        var result = await flow.CompleteAsync(request.State, request.Code, request.User, true, cancellationToken);
        if (result.IsFailure)
            return Unauthorized();
        var context = JsonSerializer.Deserialize<ConsentEntry>(result.Value.Request.McpContext!);
        if (context is null || !IsRedirectUriAllowed(context.RedirectUri))
            return Unauthorized();
        var client = await ValidateClientAsync(context.ClientId, context.RedirectUri, cancellationToken);
        if (client.Error is not null)
            return BadRequest(new { error = client.Error });
        var ticket = authStore.CreatePendingConsent(context with { UserId = result.Value.Login.UserId, ClientName = client.ClientName });
        Response.Headers.Location = $"/oauth/consent?ticket={Uri.EscapeDataString(ticket)}";
        return StatusCode(StatusCodes.Status303SeeOther);
    }
}
