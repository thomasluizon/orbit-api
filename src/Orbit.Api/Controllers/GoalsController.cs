using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class GoalsController(IMediator mediator, ILogger<GoalsController> logger) : ControllerBase
{
    public record CreateGoalRequest(string Title, string? Description, decimal TargetValue, string Unit, DateOnly? Deadline = null);
    public record UpdateGoalRequest(string Title, string? Description, decimal TargetValue, string Unit, DateOnly? Deadline = null);
    public record UpdateProgressRequest(decimal CurrentValue, string? Note = null);
    public record UpdateStatusRequest(GoalStatus Status);
    public record ReorderGoalsRequest(IReadOnlyList<GoalPositionRequest> Positions);
    public record GoalPositionRequest(Guid Id, int Position);
    public record LinkHabitsRequest(List<Guid> HabitIds);

    [HttpGet]
    public async Task<IActionResult> GetGoals(
        [FromQuery] GoalStatus? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var query = new GetGoalsQuery(HttpContext.GetUserId(), status, page, pageSize);
        var result = await mediator.Send(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetGoalById(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetGoalByIdQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error });
    }

    [HttpPost]
    public async Task<IActionResult> CreateGoal([FromBody] CreateGoalRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateGoalCommand(HttpContext.GetUserId(), request.Title, request.Description, request.TargetValue, request.Unit, request.Deadline);
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsSuccess)
        {
            logger.LogInformation("Goal created {GoalId} by user {UserId}", result.Value, HttpContext.GetUserId());
            return CreatedAtAction(nameof(GetGoalById), new { id = result.Value }, new { id = result.Value });
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateGoal(Guid id, [FromBody] UpdateGoalRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateGoalCommand(HttpContext.GetUserId(), id, request.Title, request.Description, request.TargetValue, request.Unit, request.Deadline);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}/progress")]
    public async Task<IActionResult> UpdateProgress(Guid id, [FromBody] UpdateProgressRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateGoalProgressCommand(HttpContext.GetUserId(), id, request.CurrentValue, request.Note);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateGoalStatusCommand(HttpContext.GetUserId(), id, request.Status);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPut("reorder")]
    public async Task<IActionResult> ReorderGoals([FromBody] ReorderGoalsRequest request, CancellationToken cancellationToken)
    {
        var positions = request.Positions.Select(p => new GoalPositionUpdate(p.Id, p.Position)).ToList();
        var command = new ReorderGoalsCommand(HttpContext.GetUserId(), positions);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPut("{goalId:guid}/habits")]
    public async Task<IActionResult> LinkHabits(
        Guid goalId,
        [FromBody] LinkHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new LinkHabitsToGoalCommand(HttpContext.GetUserId(), goalId, request.HabitIds);
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsSuccess)
        {
            logger.LogInformation("Linked {Count} habits to goal {GoalId} by user {UserId}", request.HabitIds.Count, goalId, HttpContext.GetUserId());
            return NoContent();
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpGet("{id:guid}/metrics")]
    public async Task<IActionResult> GetGoalMetrics(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetGoalMetricsQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpGet("review")]
    public async Task<IActionResult> GetGoalReview(
        [FromQuery] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var query = new GetGoalReviewQuery(HttpContext.GetUserId(), language);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteGoal(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteGoalCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsSuccess)
        {
            logger.LogInformation("Goal deleted {GoalId} by user {UserId}", id, HttpContext.GetUserId());
            return NoContent();
        }
        return BadRequest(new { error = result.Error });
    }
}
