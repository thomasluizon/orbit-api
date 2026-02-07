using MediatR;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Middleware;
using Orbit.Application.Tasks.Commands;
using Orbit.Application.Tasks.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TasksController(IMediator mediator) : ControllerBase
{
    public record CreateTaskRequest(string Title, string? Description, DateOnly? DueDate);
    public record UpdateTaskStatusRequest(TaskItemStatus NewStatus);

    [HttpGet]
    public async Task<IActionResult> GetTasks(
        [FromQuery] bool includeCompleted = false,
        CancellationToken cancellationToken = default)
    {
        var query = new GetTasksQuery(HttpContext.GetUserId(), includeCompleted);
        var tasks = await mediator.Send(query, cancellationToken);
        return Ok(tasks);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTask(
        [FromBody] CreateTaskRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateTaskCommand(
            HttpContext.GetUserId(),
            request.Title,
            request.Description,
            request.DueDate);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetTasks), new { id = result.Value }, result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> UpdateTaskStatus(
        Guid id,
        [FromBody] UpdateTaskStatusRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateTaskCommand(
            HttpContext.GetUserId(),
            id,
            request.NewStatus);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }
}
