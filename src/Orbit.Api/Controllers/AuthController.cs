using MediatR;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Auth.Commands;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(IMediator mediator, ILogger<AuthController> logger) : ControllerBase
{
    public record SendCodeRequest(string Email, string Language = "en");
    public record VerifyCodeRequest(string Email, string Code, string Language = "en");
    public record GoogleAuthRequest(string AccessToken, string Language = "en");

    [HttpPost("send-code")]
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
    public async Task<IActionResult> VerifyCode(
        [FromBody] VerifyCodeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new VerifyCodeCommand(request.Email, request.Code, request.Language);
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
    public async Task<IActionResult> GoogleAuth(
        [FromBody] GoogleAuthRequest request,
        CancellationToken cancellationToken)
    {
        var command = new GoogleAuthCommand(request.AccessToken, request.Language);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("User logged in via Google");
            return Ok(result.Value);
        }

        logger.LogWarning("Google auth failed: {Error}", result.Error);
        return Unauthorized(new { error = result.Error });
    }
}
