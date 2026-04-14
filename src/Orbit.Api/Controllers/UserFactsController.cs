using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Queries;
using Orbit.Domain.Common;

#pragma warning disable CA1873

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/user-facts")]
public partial class UserFactsController(IMediator mediator, ILogger<UserFactsController> logger) : ControllerBase
{
    public record BulkDeleteUserFactsRequest(IReadOnlyList<Guid> FactIds);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUserFacts(CancellationToken cancellationToken)
    {
        var query = new GetUserFactsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteUserFact(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteUserFactCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsFailure && result.ErrorCode == Result.PayGateErrorCode)
            return result.ToPayGateAwareResult(() => NoContent());

        if (result.IsSuccess)
            LogUserFactDeleted(logger, id, HttpContext.GetUserId());

        return result.IsSuccess
            ? NoContent()
            : NotFound(new { error = result.Error });
    }

    [HttpDelete("bulk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> BulkDeleteUserFacts(
        [FromBody] BulkDeleteUserFactsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new BulkDeleteUserFactsCommand(HttpContext.GetUserId(), request.FactIds);
        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(value => Ok(new { deleted = value }));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "User fact deleted {FactId} by user {UserId}")]
    private static partial void LogUserFactDeleted(ILogger logger, Guid factId, Guid userId);

}
