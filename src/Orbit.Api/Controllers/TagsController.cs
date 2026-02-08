using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class TagsController(IMediator mediator) : ControllerBase
{
    public record CreateTagRequest(string Name, string Color);

    [HttpGet]
    public async Task<IActionResult> GetTags(CancellationToken cancellationToken)
    {
        var query = new GetTagsQuery(HttpContext.GetUserId());
        var tags = await mediator.Send(query, cancellationToken);
        return Ok(tags);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTag(
        [FromBody] CreateTagRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateTagCommand(
            HttpContext.GetUserId(),
            request.Name,
            request.Color);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetTags), new { id = result.Value }, result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTag(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteTagCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("/api/habits/{habitId:guid}/tags/{tagId:guid}")]
    public async Task<IActionResult> AssignTag(
        Guid habitId,
        Guid tagId,
        CancellationToken cancellationToken)
    {
        var command = new AssignTagCommand(HttpContext.GetUserId(), habitId, tagId);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }

    [HttpDelete("/api/habits/{habitId:guid}/tags/{tagId:guid}")]
    public async Task<IActionResult> UnassignTag(
        Guid habitId,
        Guid tagId,
        CancellationToken cancellationToken)
    {
        var command = new UnassignTagCommand(HttpContext.GetUserId(), habitId, tagId);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }
}
