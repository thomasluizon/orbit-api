using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/api-keys")]
public partial class ApiKeysController(IMediator mediator, ILogger<ApiKeysController> logger) : ControllerBase
{
    public record CreateApiKeyRequest(
        string Name,
        IReadOnlyList<string>? Scopes = null,
        bool IsReadOnly = false,
        DateTime? ExpiresAtUtc = null);
    public record ConfirmApiKeyCreationChallengeRequest(string Code);

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> CreateApiKey(
        [FromBody] CreateApiKeyRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateApiKeyCommand(
            HttpContext.GetUserId(),
            request.Name,
            request.Scopes,
            request.IsReadOnly,
            request.ExpiresAtUtc);
        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(value =>
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogApiKeyCreated(value.Id, HttpContext.GetUserId());
            return Created($"/api/api-keys/{value.Id}", value);
        });
    }

    [HttpPost("creation-challenge")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RequestCreationChallenge(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new RequestApiKeyCreationChallengeCommand(HttpContext.GetUserId()),
            cancellationToken);
        return result.ToPayGateAwareResult(() => Ok(new { message = "API key creation code sent" }));
    }

    [HttpPost("creation-challenge/confirm")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ConfirmCreationChallenge(
        [FromBody] ConfirmApiKeyCreationChallengeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new ConfirmApiKeyCreationChallengeCommand(HttpContext.GetUserId(), request.Code),
            cancellationToken);
        return result.ToPayGateAwareResult(() => Ok(new { message = "API key creation confirmed" }));
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> GetApiKeys(CancellationToken cancellationToken)
    {
        var query = new GetApiKeysQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> RevokeApiKey(Guid id, CancellationToken cancellationToken)
    {
        var command = new RevokeApiKeyCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogApiKeyRevoked(id, HttpContext.GetUserId());
            return NoContent();
        }
        return result.ToErrorResult();
    }
}

internal static partial class ApiKeysControllerLogging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "API key created {KeyId} by user {UserId}")]
    internal static partial void LogApiKeyCreated(this ILogger logger, Guid keyId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "API key revoked {KeyId} by user {UserId}")]
    internal static partial void LogApiKeyRevoked(this ILogger logger, Guid keyId, Guid userId);
}
