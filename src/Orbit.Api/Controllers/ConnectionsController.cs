using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Connections;

namespace Orbit.Api.Controllers;

[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Authorize]
[Route("api/connections/notion")]
public sealed class ConnectionsController(ISender sender, ILogger<ConnectionsController> logger) : ControllerBase
{
    public sealed record AuthorizeRequest(string RedirectUri);
    public sealed record CallbackRequest(string Code, string State, string RedirectUri);

    [HttpPost("authorize")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType<NotionAuthorizationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AuthorizeNotion([FromBody] AuthorizeRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new AuthorizeNotionCommand(HttpContext.GetUserId(), request.RedirectUri), ct);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpPost("callback")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CompleteNotion([FromBody] CallbackRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CompleteNotionCommand(HttpContext.GetUserId(), request.Code, request.State, request.RedirectUri), ct);
        if (result.IsSuccess) logger.LogDebug("Notion authorization completed for {UserId}", HttpContext.GetUserId());
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpGet]
    [ProducesResponseType<NotionConnectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetNotion(CancellationToken ct)
    {
        var result = await sender.Send(new GetNotionConnectionQuery(HttpContext.GetUserId()), ct);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DisconnectNotion(CancellationToken ct)
    {
        var result = await sender.Send(new DisconnectNotionCommand(HttpContext.GetUserId()), ct);
        return result.ToPayGateAwareResult(() => NoContent());
    }
}
