using MediatR;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(IMediator mediator) : ControllerBase
{
    public record RegisterRequest(string Name, string Email, string Password, string Language = "en");
    public record LoginRequest(string Email, string Password);
    public record GoogleAuthRequest(string AccessToken, string Language = "en");

    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var command = new RegisterCommand(request.Name, request.Email, request.Password, request.Language);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(new { userId = result.Value, message = "Registration successful" })
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var query = new LoginQuery(request.Email, request.Password);
        var result = await mediator.Send(query, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : Unauthorized(new { error = result.Error });
    }

    [HttpPost("google")]
    public async Task<IActionResult> GoogleAuth(
        [FromBody] GoogleAuthRequest request,
        CancellationToken cancellationToken)
    {
        var command = new GoogleAuthCommand(request.AccessToken, request.Language);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : Unauthorized(new { error = result.Error });
    }
}
