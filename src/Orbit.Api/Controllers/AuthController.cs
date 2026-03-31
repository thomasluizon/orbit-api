using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Orbit.Api.Extensions;
using Orbit.Application.Auth.Commands;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(IMediator mediator, ILogger<AuthController> logger) : ControllerBase
{
    public record SendCodeRequest(string Email, string Language = "en");
    public record VerifyCodeRequest(string Email, string Code, string Language = "en", string? ReferralCode = null);
    public record GoogleAuthRequest(string AccessToken, string Language = "en", string? GoogleAccessToken = null, string? GoogleRefreshToken = null, string? ReferralCode = null);
    public record ConfirmDeletionRequest(string Code);

    [HttpPost("send-code")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> SendCode(
        [FromBody] SendCodeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SendCodeCommand(request.Email, request.Language);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("Verification code sent to {Email}", request.Email);
            return Ok(new { message = "Verification code sent" });
        }

        logger.LogWarning("Failed to send code to {Email}: {Error}", request.Email, result.Error);
        return BadRequest(new { error = result.Error });
    }

    [HttpPost("verify-code")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> VerifyCode(
        [FromBody] VerifyCodeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new VerifyCodeCommand(request.Email, request.Code, request.Language, request.ReferralCode);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("User logged in via code {Email}", request.Email);
            return Ok(result.Value);
        }

        logger.LogWarning("Code verification failed for {Email}: {Error}", request.Email, result.Error);
        return Unauthorized(new { error = result.Error });
    }

    [HttpPost("google")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> GoogleAuth(
        [FromBody] GoogleAuthRequest request,
        CancellationToken cancellationToken)
    {
        var command = new GoogleAuthCommand(request.AccessToken, request.Language, request.GoogleAccessToken, request.GoogleRefreshToken, request.ReferralCode);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("User logged in via Google");
            return Ok(result.Value);
        }

        logger.LogWarning("Google auth failed: {Error}", result.Error);
        return Unauthorized(new { error = result.Error });
    }

    [Authorize]
    [HttpPost("request-deletion")]
    public async Task<IActionResult> RequestDeletion(CancellationToken cancellationToken)
    {
        var command = new RequestAccountDeletionCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("Account deletion requested by {UserId}", HttpContext.GetUserId());
            return Ok(new { message = "Deletion code sent" });
        }

        logger.LogWarning("Deletion request failed for {UserId}: {Error}", HttpContext.GetUserId(), result.Error);
        return BadRequest(new { error = result.Error });
    }

    [Authorize]
    [HttpPost("confirm-deletion")]
    public async Task<IActionResult> ConfirmDeletion(
        [FromBody] ConfirmDeletionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ConfirmAccountDeletionCommand(HttpContext.GetUserId(), request.Code);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("Account deactivated for {UserId}, scheduled deletion at {ScheduledAt}", HttpContext.GetUserId(), result.Value);
            return Ok(new { message = "Account deactivated", scheduledDeletionAt = result.Value });
        }

        logger.LogWarning("Deletion confirmation failed for {UserId}: {Error}", HttpContext.GetUserId(), result.Error);
        return BadRequest(new { error = result.Error });
    }
}
