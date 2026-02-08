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
        FrequencyUnit FrequencyUnit,
        int FrequencyQuantity,
        HabitType Type,
        string? Unit,
        decimal? TargetValue,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsNegative = false,
        IReadOnlyList<string>? SubHabits = null);

    public record LogHabitRequest(DateOnly Date, decimal? Value, string? Note = null);

    public record AddSubHabitRequest(string Title, int SortOrder = 0);

    public record SubHabitCompletionRequest(Guid SubHabitId, bool IsCompleted);

    public record LogSubHabitsRequest(DateOnly Date, IReadOnlyList<SubHabitCompletionRequest> Completions);

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
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Type,
            request.Unit,
            request.TargetValue,
            request.Days,
            request.IsNegative,
            request.SubHabits);

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

    [HttpPost("{id:guid}/sub-habits")]
    public async Task<IActionResult> AddSubHabit(
        Guid id,
        [FromBody] AddSubHabitRequest request,
        CancellationToken cancellationToken)
    {
        var command = new AddSubHabitCommand(
            HttpContext.GetUserId(),
            id,
            request.Title,
            request.SortOrder);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(new { subHabitId = result.Value })
            : BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}/sub-habits/{subHabitId:guid}")]
    public async Task<IActionResult> RemoveSubHabit(
        Guid id,
        Guid subHabitId,
        CancellationToken cancellationToken)
    {
        var command = new RemoveSubHabitCommand(
            HttpContext.GetUserId(),
            id,
            subHabitId);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("{id:guid}/sub-habits/log")]
    public async Task<IActionResult> LogSubHabits(
        Guid id,
        [FromBody] LogSubHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var completions = request.Completions
            .Select(c => new SubHabitCompletion(c.SubHabitId, c.IsCompleted))
            .ToList();

        var command = new LogSubHabitCommand(
            HttpContext.GetUserId(),
            id,
            request.Date,
            completions);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok()
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
}
