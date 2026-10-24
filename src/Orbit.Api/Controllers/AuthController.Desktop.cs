using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;

namespace Orbit.Api.Controllers;

public partial class AuthController
{
    public sealed record DesktopHandoffRequest(string State, string CodeChallenge, string CodeChallengeMethod, string RedirectUri);
    public sealed record DesktopExchangeRequest(string Code, string State, string CodeVerifier, string RedirectUri);

    [Authorize]
    [HttpPost("desktop/handoff")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType<DesktopHandoffResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateDesktopHandoff([FromBody] DesktopHandoffRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateDesktopHandoffCommand(HttpContext.GetUserId(), request.State,
            request.CodeChallenge, request.CodeChallengeMethod, request.RedirectUri), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [AllowAnonymous]
    [HttpPost("desktop/exchange")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExchangeDesktopHandoff([FromBody] DesktopExchangeRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ExchangeDesktopHandoffCommand(request.Code, request.State,
            request.CodeVerifier, request.RedirectUri), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value), StatusCodes.Status401Unauthorized);
    }
}
