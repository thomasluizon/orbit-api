using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Queries;

#pragma warning disable CA1873

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/api-keys")]
public partial class ApiKeysController(IMediator mediator, ILogger<ApiKeysController> logger) : ControllerBase
{
    public record CreateApiKeyRequest(string Name);

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateApiKey(
        [FromBody] CreateApiKeyRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateApiKeyCommand(HttpContext.GetUserId(), request.Name);
        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(value =>
        {
            LogApiKeyCreated(logger, value.Id, HttpContext.GetUserId());
            return Created($"/api/api-keys/{value.Id}", value);
        });
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetApiKeys(CancellationToken cancellationToken)
    {
        var query = new GetApiKeysQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeApiKey(Guid id, CancellationToken cancellationToken)
    {
        var command = new RevokeApiKeyCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogApiKeyRevoked(logger, id, HttpContext.GetUserId());
            return NoContent();
        }
        return NotFound(new { error = result.Error });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "API key created {KeyId} by user {UserId}")]
    private static partial void LogApiKeyCreated(ILogger logger, Guid keyId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "API key revoked {KeyId} by user {UserId}")]
    private static partial void LogApiKeyRevoked(ILogger logger, Guid keyId, Guid userId);
}
