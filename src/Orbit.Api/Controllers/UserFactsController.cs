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
    public record CreateUserFactRequest(string FactText, string? Category);
    public record UpdateUserFactRequest(string FactText, string? Category);

    [HttpGet]
    public async Task<IActionResult> GetUserFacts(CancellationToken cancellationToken)
    {
        var query = new GetUserFactsQuery(HttpContext.GetUserId());
        var facts = await mediator.Send(query, cancellationToken);
        return Ok(facts);
    }

    [HttpPost]
    public async Task<IActionResult> CreateUserFact(
        [FromBody] CreateUserFactRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateUserFactCommand(
            HttpContext.GetUserId(),
            request.FactText,
            request.Category);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Created($"/api/user-facts/{result.Value}", new { id = result.Value })
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateUserFact(
        Guid id,
        [FromBody] UpdateUserFactRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateUserFactCommand(
            HttpContext.GetUserId(),
            id,
            request.FactText,
            request.Category);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
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
}
