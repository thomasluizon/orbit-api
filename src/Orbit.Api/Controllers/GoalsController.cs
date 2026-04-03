using System.Text.Json.Serialization;
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
public partial class GoalsController(IMediator mediator, ILogger<GoalsController> logger) : ControllerBase
{
    public record CreateGoalRequest(string Title, string? Description, [property: JsonRequired] decimal TargetValue, string Unit, DateOnly? Deadline = null);
    public record UpdateGoalRequest(string Title, string? Description, [property: JsonRequired] decimal TargetValue, string Unit, DateOnly? Deadline = null);
    public record UpdateProgressRequest([property: JsonRequired] decimal CurrentValue, string? Note = null);
    public record UpdateStatusRequest([property: JsonRequired] GoalStatus Status);
    public record ReorderGoalsRequest(IReadOnlyList<GoalPositionRequest> Positions);
    public record GoalPositionRequest(Guid Id, int Position);
    public record LinkHabitsRequest(List<Guid> HabitIds);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetGoals(
        [FromQuery] GoalStatus? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var query = new GetGoalsQuery(HttpContext.GetUserId(), status, page, pageSize);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGoalById(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetGoalByIdQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error });
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateGoal([FromBody] CreateGoalRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateGoalCommand(HttpContext.GetUserId(), request.Title, request.Description, request.TargetValue, request.Unit, request.Deadline);
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsSuccess)
        {
            LogGoalCreated(logger, result.Value, HttpContext.GetUserId());
            return CreatedAtAction(nameof(GetGoalById), new { id = result.Value }, new { id = result.Value });
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateGoal(Guid id, [FromBody] UpdateGoalRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateGoalCommand(HttpContext.GetUserId(), id, request.Title, request.Description, request.TargetValue, request.Unit, request.Deadline);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}/progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateProgress(Guid id, [FromBody] UpdateProgressRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateGoalProgressCommand(HttpContext.GetUserId(), id, request.CurrentValue, request.Note);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}/status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateGoalStatusCommand(HttpContext.GetUserId(), id, request.Status);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPut("reorder")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReorderGoals([FromBody] ReorderGoalsRequest request, CancellationToken cancellationToken)
    {
        var positions = request.Positions.Select(p => new GoalPositionUpdate(p.Id, p.Position)).ToList();
        var command = new ReorderGoalsCommand(HttpContext.GetUserId(), positions);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPut("{goalId:guid}/habits")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LinkHabits(
        Guid goalId,
        [FromBody] LinkHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new LinkHabitsToGoalCommand(HttpContext.GetUserId(), goalId, request.HabitIds);
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsSuccess)
        {
            LogLinkedHabitsToGoal(logger, request.HabitIds.Count, goalId, HttpContext.GetUserId());
            return NoContent();
        }
        return BadRequest(new { error = result.Error });
    }

    [HttpGet("{id:guid}/detail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGoalDetail(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetGoalDetailQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error });
    }

    [HttpGet("{id:guid}/metrics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetGoalMetrics(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetGoalMetricsQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpGet("review")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetGoalReview(
        [FromQuery] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var query = new GetGoalReviewQuery(HttpContext.GetUserId(), language);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteGoal(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteGoalCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsSuccess)
        {
            LogGoalDeleted(logger, id, HttpContext.GetUserId());
            return NoContent();
        }
        return BadRequest(new { error = result.Error });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Goal created {GoalId} by user {UserId}")]
    private static partial void LogGoalCreated(ILogger logger, Guid goalId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Linked {Count} habits to goal {GoalId} by user {UserId}")]
    private static partial void LogLinkedHabitsToGoal(ILogger logger, int count, Guid goalId, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Goal deleted {GoalId} by user {UserId}")]
    private static partial void LogGoalDeleted(ILogger logger, Guid goalId, Guid userId);

}
