using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;

namespace Orbit.Api.Controllers;

public partial class AuthController
{
    private const string AppleCacheControl = "no-store";

    public record AppleNativeRequest(string IdentityToken, string AuthorizationCode, string RawNonce,
        string? GivenName = null, string? FamilyName = null, string Language = "en", string? ReferralCode = null);
    public record AppleStartRequest(string State, string Language = "en", string? ReferralCode = null);
    public record AppleStartResponse(string AuthorizationUrl);
    public record AppleHandoffRequest(string Handoff, string State);
    public sealed class AppleCallbackRequest
    {
        [FromForm(Name = "state")] public string State { get; init; } = "";
        [FromForm(Name = "code")] public string? Code { get; init; }
        [FromForm(Name = "user")] public string? User { get; init; }
        [FromForm(Name = "error")] public string? Error { get; init; }
    }

    [HttpPost("apple/native")]
    [AllowAnonymous]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AppleNative([FromBody] AppleNativeRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = AppleCacheControl;
        var result = await mediator.Send(new AppleNativeSignInCommand(request.IdentityToken, request.AuthorizationCode,
            request.RawNonce, request.GivenName, request.FamilyName, request.Language, request.ReferralCode), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : result.ToErrorResult(StatusCodes.Status401Unauthorized);
    }

    [HttpPost("apple/start")]
    [AllowAnonymous]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(typeof(AppleStartResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AppleStart([FromBody] AppleStartRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = AppleCacheControl;
        var url = await mediator.Send(new StartAppleSignInCommand(request.State, request.Language, request.ReferralCode), cancellationToken);
        return url is null ? StatusCode(StatusCodes.Status503ServiceUnavailable) : Ok(new AppleStartResponse(url));
    }

    [HttpPost("apple/callback")]
    [AllowAnonymous]
    [DistributedRateLimit("auth")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(StatusCodes.Status303SeeOther)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AppleCallback([FromForm] AppleCallbackRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = AppleCacheControl;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (request.Error is not null || string.IsNullOrEmpty(request.Code))
            return Unauthorized();
        var result = await mediator.Send(new CompleteAppleCallbackCommand(request.State, request.Code, request.User), cancellationToken);
        if (result.IsFailure)
            return result.ToErrorResult(StatusCodes.Status401Unauthorized);
        Response.Headers.Location = result.Value;
        return StatusCode(StatusCodes.Status303SeeOther);
    }

    [HttpPost("apple/handoff")]
    [AllowAnonymous]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AppleHandoff([FromBody] AppleHandoffRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = AppleCacheControl;
        var result = await mediator.Send(new TakeAppleHandoffCommand(request.Handoff, request.State), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : result.ToErrorResult(StatusCodes.Status401Unauthorized);
    }
}
