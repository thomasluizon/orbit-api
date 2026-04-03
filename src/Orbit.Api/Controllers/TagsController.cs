using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;

#pragma warning disable CA1873

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class TagsController(IMediator mediator, ILogger<TagsController> logger) : ControllerBase
{
    public record CreateTagRequest(string Name, string Color);
    public record UpdateTagRequest(string Name, string Color);
    public record AssignTagsRequest(IReadOnlyList<Guid> TagIds);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTags(CancellationToken cancellationToken)
    {
        var query = new GetTagsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateTag(
        [FromBody] CreateTagRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateTagCommand(HttpContext.GetUserId(), request.Name, request.Color);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogTagCreated(logger, result.Value, HttpContext.GetUserId());
            return Created($"/api/tags/{result.Value}", new { id = result.Value });
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateTag(
        Guid id,
        [FromBody] UpdateTagRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateTagCommand(HttpContext.GetUserId(), id, request.Name, request.Color);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogTagUpdated(logger, id, HttpContext.GetUserId());
            return NoContent();
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteTag(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteTagCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogTagDeleted(logger, id, HttpContext.GetUserId());
            return NoContent();
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpPut("{habitId:guid}/assign")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AssignTags(
        Guid habitId,
        [FromBody] AssignTagsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new AssignTagsCommand(HttpContext.GetUserId(), habitId, request.TagIds);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Tag created {TagId} by user {UserId}")]
    private static partial void LogTagCreated(ILogger logger, Guid tagId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Tag updated {TagId} by user {UserId}")]
    private static partial void LogTagUpdated(ILogger logger, Guid tagId, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Tag deleted {TagId} by user {UserId}")]
    private static partial void LogTagDeleted(ILogger logger, Guid tagId, Guid userId);

}
