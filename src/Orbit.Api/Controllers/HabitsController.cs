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
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        IReadOnlyList<string>? SubHabits = null,
        DateOnly? DueDate = null,
        TimeOnly? DueTime = null,
        bool ReminderEnabled = false,
        int ReminderMinutesBefore = 15);

    public record UpdateHabitRequest(
        string Title,
        string? Description,
        FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        DateOnly? DueDate = null,
        TimeOnly? DueTime = null,
        bool? ReminderEnabled = null,
        int? ReminderMinutesBefore = null);

    public record LogHabitRequest(string? Note = null);

    public record BulkCreateHabitsRequest(IReadOnlyList<BulkHabitItemRequest> Habits);

    public record BulkHabitItemRequest(
        string Title,
        string? Description,
        FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        DateOnly? DueDate = null,
        IReadOnlyList<BulkHabitItemRequest>? SubHabits = null);

    public record BulkDeleteHabitsRequest(IReadOnlyList<Guid> HabitIds);

    public record ReorderHabitsRequest(IReadOnlyList<HabitPositionRequest> Positions);

    public record HabitPositionRequest(Guid HabitId, int Position);

    public record MoveHabitParentRequest(Guid? ParentId);

    public record CreateSubHabitRequest(string Title, string? Description);

    [HttpGet]
    public async Task<IActionResult> GetHabits(
        [FromQuery] DateOnly dateFrom,
        [FromQuery] DateOnly dateTo,
        [FromQuery] bool includeOverdue = false,
        [FromQuery] string? search = null,
        [FromQuery] string? frequencyUnit = null,
        [FromQuery] bool? isCompleted = null,
        [FromQuery] Guid[]? tagIds = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var query = new GetHabitScheduleQuery(
            HttpContext.GetUserId(),
            dateFrom,
            dateTo,
            includeOverdue,
            search,
            frequencyUnit,
            isCompleted,
            tagIds is { Length: > 0 } ? tagIds : null,
            page,
            pageSize);
        var result = await mediator.Send(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetDailySummary(
        [FromQuery] DateOnly dateFrom,
        [FromQuery] DateOnly dateTo,
        [FromQuery] bool includeOverdue = false,
        [FromQuery] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var query = new GetDailySummaryQuery(
            HttpContext.GetUserId(),
            dateFrom,
            dateTo,
            includeOverdue,
            language);

        var result = await mediator.Send(query, cancellationToken);

        if (result.IsSuccess)
            return Ok(result.Value);

        return result.ErrorCode == "PAY_GATE"
            ? StatusCode(403, new { error = result.Error, code = "PAY_GATE" })
            : BadRequest(new { error = result.Error });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetHabitById(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitByIdQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error });
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
            request.Days,
            request.IsBadHabit,
            request.SubHabits,
            request.DueDate,
            request.DueTime,
            request.ReminderEnabled,
            request.ReminderMinutesBefore);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            return CreatedAtAction(nameof(GetHabits), new { id = result.Value }, result.Value);

        return result.ErrorCode == "PAY_GATE"
            ? StatusCode(403, new { error = result.Error, code = "PAY_GATE" })
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("{id:guid}/log")]
    public async Task<IActionResult> LogHabit(
        Guid id,
        [FromBody] LogHabitRequest? request,
        CancellationToken cancellationToken)
    {
        var command = new LogHabitCommand(
            HttpContext.GetUserId(),
            id,
            request?.Note);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(new { logId = result.Value })
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateHabit(
        Guid id,
        [FromBody] UpdateHabitRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateHabitCommand(
            HttpContext.GetUserId(),
            id,
            request.Title,
            request.Description,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Days,
            request.IsBadHabit,
            request.DueDate,
            request.DueTime,
            request.ReminderEnabled,
            request.ReminderMinutesBefore);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
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

    [HttpGet("{id:guid}/logs")]
    public async Task<IActionResult> GetLogs(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitLogsQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpGet("{id:guid}/metrics")]
    public async Task<IActionResult> GetMetrics(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitMetricsQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkCreate(
        [FromBody] BulkCreateHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var habits = request.Habits.Select(MapToBulkHabitItem).ToList();

        var command = new BulkCreateHabitsCommand(
            HttpContext.GetUserId(),
            habits);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            return Ok(result.Value);

        return result.ErrorCode == "PAY_GATE"
            ? StatusCode(403, new { error = result.Error, code = "PAY_GATE" })
            : BadRequest(new { error = result.Error });
    }

    [HttpDelete("bulk")]
    public async Task<IActionResult> BulkDelete(
        [FromBody] BulkDeleteHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new BulkDeleteHabitsCommand(
            HttpContext.GetUserId(),
            request.HabitIds);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("reorder")]
    public async Task<IActionResult> ReorderHabits(
        [FromBody] ReorderHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var positions = request.Positions
            .Select(p => new HabitPositionUpdate(p.HabitId, p.Position))
            .ToList();

        var command = new ReorderHabitsCommand(HttpContext.GetUserId(), positions);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}/parent")]
    public async Task<IActionResult> MoveHabitParent(
        Guid id,
        [FromBody] MoveHabitParentRequest request,
        CancellationToken cancellationToken)
    {
        var command = new MoveHabitParentCommand(HttpContext.GetUserId(), id, request.ParentId);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("{id:guid}/duplicate")]
    public async Task<IActionResult> DuplicateHabit(
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = new DuplicateHabitCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Created($"/api/habits/{result.Value}", new { id = result.Value })
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("{parentId:guid}/sub-habits")]
    public async Task<IActionResult> CreateSubHabit(
        Guid parentId,
        [FromBody] CreateSubHabitRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateSubHabitCommand(
            HttpContext.GetUserId(),
            parentId,
            request.Title,
            request.Description);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            return Created($"/api/habits/{result.Value}", new { id = result.Value });

        return result.ErrorCode == "PAY_GATE"
            ? StatusCode(403, new { error = result.Error, code = "PAY_GATE" })
            : BadRequest(new { error = result.Error });
    }

    private static BulkHabitItem MapToBulkHabitItem(BulkHabitItemRequest request)
    {
        return new BulkHabitItem(
            request.Title,
            request.Description,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Days,
            request.IsBadHabit,
            request.DueDate,
            request.SubHabits?.Select(MapToBulkHabitItem).ToList());
    }

}
