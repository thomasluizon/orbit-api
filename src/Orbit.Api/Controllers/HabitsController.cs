using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class HabitsController(IMediator mediator) : ControllerBase
{
    public record CreateHabitRequest(
        string Title,
        string? Description,
        FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        HabitType Type,
        string? Unit,
        decimal? TargetValue,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsNegative = false,
        IReadOnlyList<string>? SubHabits = null,
        DateOnly? DueDate = null);

    public record LogHabitRequest(DateOnly Date, decimal? Value, string? Note = null);

    [HttpGet]
    public async Task<IActionResult> GetHabits(
        [FromQuery] string? tags,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid>? tagIds = null;

        if (!string.IsNullOrWhiteSpace(tags))
        {
            tagIds = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => Guid.TryParse(s, out _))
                .Select(Guid.Parse)
                .ToList();
        }

        var query = new GetHabitsQuery(HttpContext.GetUserId(), tagIds);
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
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Type,
            request.Unit,
            request.TargetValue,
            request.Days,
            request.IsNegative,
            request.SubHabits,
            request.DueDate);

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
            request.Value,
            request.Note);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(new { logId = result.Value })
            : BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteHabit(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteHabitCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpGet("{id:guid}/metrics")]
    public async Task<IActionResult> GetMetrics(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitMetricsQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpGet("{id:guid}/trends")]
    public async Task<IActionResult> GetTrends(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitTrendQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }
}
