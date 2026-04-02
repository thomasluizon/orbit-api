using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/checklist-templates")]
public class ChecklistTemplatesController(IMediator mediator, ILogger<ChecklistTemplatesController> logger) : ControllerBase
{
    public record CreateTemplateRequest(string Name, IReadOnlyList<string> Items);

    [HttpGet]
    public async Task<IActionResult> GetTemplates(CancellationToken cancellationToken)
    {
        var query = new GetChecklistTemplatesQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpPost]
    public async Task<IActionResult> CreateTemplate(
        [FromBody] CreateTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateChecklistTemplateCommand(HttpContext.GetUserId(), request.Name, request.Items);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("ChecklistTemplate created {TemplateId} by user {UserId}", result.Value, HttpContext.GetUserId());
            return Created($"/api/checklist-templates/{result.Value}", new { id = result.Value });
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteChecklistTemplateCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("ChecklistTemplate deleted {TemplateId} by user {UserId}", id, HttpContext.GetUserId());
            return NoContent();
        }
        return BadRequest(new { error = result.Error });
    }
}
