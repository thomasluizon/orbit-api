using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/user-facts")]
public class UserFactsController(IMediator mediator) : ControllerBase
{
    public record BulkDeleteUserFactsRequest(IReadOnlyList<Guid> FactIds);

    [HttpGet]
    public async Task<IActionResult> GetUserFacts(CancellationToken cancellationToken)
    {
        var query = new GetUserFactsQuery(HttpContext.GetUserId());
        var facts = await mediator.Send(query, cancellationToken);
        return Ok(facts);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteUserFact(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteUserFactCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : NotFound(new { error = result.Error });
    }

    [HttpDelete("bulk")]
    public async Task<IActionResult> BulkDeleteUserFacts(
        [FromBody] BulkDeleteUserFactsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new BulkDeleteUserFactsCommand(HttpContext.GetUserId(), request.FactIds);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(new { deleted = result.Value })
            : BadRequest(new { error = result.Error });
    }
}
