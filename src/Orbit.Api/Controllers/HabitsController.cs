using MediatR;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Middleware;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HabitsController(IMediator mediator) : ControllerBase
{
    public record CreateHabitRequest(
        string Title,
        string? Description,
        HabitFrequency Frequency,
        HabitType Type,
        string? Unit,
        decimal? TargetValue);

    public record LogHabitRequest(DateOnly Date, decimal? Value);

    [HttpGet]
    public async Task<IActionResult> GetHabits(CancellationToken cancellationToken)
    {
        var query = new GetHabitsQuery(HttpContext.GetUserId());
        var habits = await mediator.Send(query, cancellationToken);
        return Ok(habits);
    }

    [HttpPost]
    public async Task<IActionResult> CreateHabit(
        [FromBody] CreateHabitRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateHabitCommand(
            HttpContext.GetUserId(),
            request.Title,
            request.Description,
            request.Frequency,
            request.Type,
            request.Unit,
            request.TargetValue);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetHabits), new { id = result.Value }, result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("{id:guid}/log")]
    public async Task<IActionResult> LogHabit(
        Guid id,
        [FromBody] LogHabitRequest request,
        CancellationToken cancellationToken)
    {
        var command = new LogHabitCommand(
            HttpContext.GetUserId(),
            id,
            request.Date,
            request.Value);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(new { logId = result.Value })
            : BadRequest(new { error = result.Error });
    }
}
