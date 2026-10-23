using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Proactive.Commands;
using Orbit.Application.Proactive.Queries;
using Orbit.Domain.Models;
using System.Text.Json;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/ai/proactive")]
public sealed class ProactiveAstraController(ISender sender) : ControllerBase
{
    private static readonly JsonSerializerOptions ClientContextOptions = new() { PropertyNameCaseInsensitive = true };

    [HttpGet("current")]
    [ProducesResponseType(typeof(ProactiveItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Current(CancellationToken cancellationToken, [FromQuery] string? clientContext = null)
    {
        AgentClientContext? parsed = null;
        if (!string.IsNullOrWhiteSpace(clientContext))
        {
            try
            {
                parsed = JsonSerializer.Deserialize<AgentClientContext>(clientContext, ClientContextOptions);
                if (parsed is null) return BadRequest(ErrorMessages.InvalidClientContext.ToErrorBody());
            }
            catch (JsonException)
            {
                return BadRequest(ErrorMessages.InvalidClientContext.ToErrorBody());
            }
        }
        var result = await sender.Send(new GetCurrentProactiveQuery(HttpContext.GetUserId(), parsed,
            HttpContext.User.GetAgentAuthMethod(), HttpContext.User.GetGrantedAgentScopes(),
            HttpContext.User.IsReadOnlyCredential()), cancellationToken);
        return result.ToPayGateAwareResult(value => value is null ? NoContent() : Ok(value));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProactiveItemStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetProactiveItemStatusQuery(HttpContext.GetUserId(), id), cancellationToken);
        return result.ToPayGateAwareResult(value => value is null
            ? new ContentResult { StatusCode = StatusCodes.Status404NotFound }
            : Ok(value));
    }

    [HttpPost("{id:guid}/opened")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Opened(Guid id, CancellationToken cancellationToken) => Record(id, false, cancellationToken);

    [HttpPost("{id:guid}/acted")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Acted(Guid id, CancellationToken cancellationToken) => Record(id, true, cancellationToken);

    private async Task<IActionResult> Record(Guid id, bool acted, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RecordProactiveOutcomeCommand(HttpContext.GetUserId(), id, acted), cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }
}
