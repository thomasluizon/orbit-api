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
public class TagsController(IMediator mediator, ILogger<TagsController> logger) : ControllerBase
{
    public record CreateTagRequest(string Name, string Color);
    public record UpdateTagRequest(string Name, string Color);
    public record AssignTagsRequest(IReadOnlyList<Guid> TagIds);

    [HttpGet]
    public async Task<IActionResult> GetTags(CancellationToken cancellationToken)
    {
        var query = new GetTagsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTag(
        [FromBody] CreateTagRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateTagCommand(HttpContext.GetUserId(), request.Name, request.Color);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("Tag created {TagId} by user {UserId}", result.Value, HttpContext.GetUserId());
            return Created($"/api/tags/{result.Value}", new { id = result.Value });
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTag(
        Guid id,
        [FromBody] UpdateTagRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateTagCommand(HttpContext.GetUserId(), id, request.Name, request.Color);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("Tag updated {TagId} by user {UserId}", id, HttpContext.GetUserId());
            return NoContent();
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTag(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteTagCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("Tag deleted {TagId} by user {UserId}", id, HttpContext.GetUserId());
            return NoContent();
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpPut("{habitId:guid}/assign")]
    public async Task<IActionResult> AssignTags(
        Guid habitId,
        [FromBody] AssignTagsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new AssignTagsCommand(HttpContext.GetUserId(), habitId, request.TagIds);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }
}
