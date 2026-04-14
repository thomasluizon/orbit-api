using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

#pragma warning disable CA1873

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class HabitsController(IMediator mediator, ILogger<HabitsController> logger, IUserDateService userDateService) : ControllerBase
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
        TimeOnly? DueEndTime = null,
        bool ReminderEnabled = false,
        IReadOnlyList<int>? ReminderTimes = null,
        IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
        bool SlipAlertEnabled = false,
        IReadOnlyList<Guid>? TagIds = null,
        IReadOnlyList<ChecklistItem>? ChecklistItems = null,
        bool IsGeneral = false,
        DateOnly? EndDate = null,
        bool IsFlexible = false,
        IReadOnlyList<Guid>? GoalIds = null,
        string? Icon = null);

    public record UpdateHabitRequest(
        string Title,
        string? Description,
        FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        DateOnly? DueDate = null,
        TimeOnly? DueTime = null,
        TimeOnly? DueEndTime = null,
        bool? ReminderEnabled = null,
        IReadOnlyList<int>? ReminderTimes = null,
        IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
        bool? SlipAlertEnabled = null,
        IReadOnlyList<ChecklistItem>? ChecklistItems = null,
        bool? IsGeneral = null,
        DateOnly? EndDate = null,
        bool? ClearEndDate = null,
        bool? IsFlexible = null,
        IReadOnlyList<Guid>? GoalIds = null,
        string? Icon = null);

    public record UpdateChecklistRequest(IReadOnlyList<ChecklistItem> ChecklistItems);

    public record LogHabitRequest(string? Note = null, DateOnly? Date = null);

    public record SkipHabitRequest(DateOnly? Date = null);

    public record BulkCreateHabitsRequest(
        IReadOnlyList<BulkHabitItemRequest> Habits,
        bool FromSyncReview = false);

    public record BulkHabitItemRequest(
        string Title,
        string? Description,
        FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        DateOnly? DueDate = null,
        TimeOnly? DueTime = null,
        TimeOnly? DueEndTime = null,
        bool ReminderEnabled = false,
        IReadOnlyList<int>? ReminderTimes = null,
        IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
        IReadOnlyList<BulkHabitItemRequest>? SubHabits = null,
        bool IsGeneral = false,
        DateOnly? EndDate = null,
        bool IsFlexible = false,
        IReadOnlyList<ChecklistItem>? ChecklistItems = null,
        string? GoogleEventId = null,
        string? Icon = null);

    public record BulkDeleteHabitsRequest(IReadOnlyList<Guid> HabitIds);

    public record BulkLogHabitItem(Guid HabitId, DateOnly? Date = null);
    public record BulkLogHabitsRequest(IReadOnlyList<BulkLogHabitItem> Items);

    public record BulkSkipHabitItem(Guid HabitId, DateOnly? Date = null);
    public record BulkSkipHabitsRequest(IReadOnlyList<BulkSkipHabitItem> Items);

    public record ReorderHabitsRequest(IReadOnlyList<HabitPositionRequest> Positions);

    public record HabitPositionRequest(Guid HabitId, int Position);

    public record MoveHabitParentRequest(Guid? ParentId);

    public record GetHabitsFilterRequest
    {
        public DateOnly? DateFrom { get; init; }
        public DateOnly? DateTo { get; init; }
        public bool? IncludeOverdue { get; init; }
        public string? Search { get; init; }
        public string? FrequencyUnit { get; init; }
        public bool? IsCompleted { get; init; }
        public Guid[]? TagIds { get; init; }
        public bool? IsGeneral { get; init; }
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 50;
        public bool? IncludeGeneral { get; init; }
    }

    public record CreateSubHabitRequest(
        string Title,
        string? Description = null,
        FrequencyUnit? FrequencyUnit = null,
        int? FrequencyQuantity = null,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        DateOnly? DueDate = null,
        TimeOnly? DueTime = null,
        TimeOnly? DueEndTime = null,
        bool ReminderEnabled = false,
        IReadOnlyList<int>? ReminderTimes = null,
        IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
        bool SlipAlertEnabled = false,
        IReadOnlyList<ChecklistItem>? ChecklistItems = null,
        IReadOnlyList<Guid>? TagIds = null,
        DateOnly? EndDate = null,
        bool IsFlexible = false,
        string? Icon = null);
    public record LinkGoalsRequest(List<Guid> GoalIds);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetHabits(
        [FromQuery] GetHabitsFilterRequest filter,
        CancellationToken cancellationToken = default)
    {
        // Defense in depth: clamp page + pageSize so a crafted ?pageSize=100000
        // cannot force the DB into a multi-second scan.
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxHabitsPageSize);

        var query = new GetHabitScheduleQuery(
            HttpContext.GetUserId(),
            filter.DateFrom,
            filter.DateTo,
            filter.IncludeOverdue ?? false,
            filter.Search,
            filter.FrequencyUnit,
            filter.IsCompleted,
            filter.TagIds is { Length: > 0 } ? filter.TagIds : null,
            filter.IsGeneral,
            page,
            pageSize,
            filter.IncludeGeneral ?? false);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    private const int MaxHabitsPageSize = 200;

    [HttpGet("calendar-month")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCalendarMonth(
        [FromQuery] DateOnly dateFrom,
        [FromQuery] DateOnly dateTo,
        CancellationToken cancellationToken = default)
    {
        var query = new GetCalendarMonthQuery(HttpContext.GetUserId(), dateFrom, dateTo);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpGet("summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("retrospective")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRetrospective(
        [FromQuery] string period,
        [FromQuery] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var today = await userDateService.GetUserTodayAsync(HttpContext.GetUserId(), cancellationToken);
        var days = period.ToLowerInvariant() switch
        {
            "week" => 7,
            "month" => 30,
            "quarter" => 90,
            "semester" => 180,
            "year" => 365,
            _ => 7
        };
        var dateFrom = today.AddDays(-days);

        var query = new GetRetrospectiveQuery(
            HttpContext.GetUserId(),
            dateFrom,
            today,
            period,
            language);

        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHabitById(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitByIdQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error });
    }

    [HttpGet("{id:guid}/detail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHabitDetail(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitFullDetailQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error });
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
            IsBadHabit: request.IsBadHabit,
            SubHabits: request.SubHabits,
            DueDate: request.DueDate,
            IsGeneral: request.IsGeneral,
            Options: new HabitCommandOptions(
                Days: request.Days,
                DueTime: request.DueTime,
                DueEndTime: request.DueEndTime,
                ReminderEnabled: request.ReminderEnabled,
                ReminderTimes: request.ReminderTimes,
                SlipAlertEnabled: request.SlipAlertEnabled,
                ChecklistItems: request.ChecklistItems,
                ScheduledReminders: request.ScheduledReminders,
                EndDate: request.EndDate,
                IsFlexible: request.IsFlexible),
            TagIds: request.TagIds,
            GoalIds: request.GoalIds,
            Icon: request.Icon);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogHabitCreated(logger, result.Value, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(v => CreatedAtAction(nameof(GetHabits), new { id = v }, new { id = v }));
    }

    [HttpPost("{id:guid}/log")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LogHabit(
        Guid id,
        [FromBody] LogHabitRequest? request,
        CancellationToken cancellationToken)
    {
        var command = new LogHabitCommand(
            HttpContext.GetUserId(),
            id,
            request?.Note,
            request?.Date);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("{id:guid}/skip")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SkipHabit(
        Guid id,
        [FromBody] SkipHabitRequest? request,
        CancellationToken cancellationToken)
    {
        var command = new SkipHabitCommand(HttpContext.GetUserId(), id, request?.Date);
        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
            IsBadHabit: request.IsBadHabit,
            DueDate: request.DueDate,
            IsGeneral: request.IsGeneral,
            ClearEndDate: request.ClearEndDate,
            Options: new UpdateHabitCommandOptions(
                Days: request.Days,
                DueTime: request.DueTime,
                DueEndTime: request.DueEndTime,
                ReminderEnabled: request.ReminderEnabled,
                ReminderTimes: request.ReminderTimes,
                SlipAlertEnabled: request.SlipAlertEnabled,
                ChecklistItems: request.ChecklistItems,
                ScheduledReminders: request.ScheduledReminders,
                EndDate: request.EndDate,
                IsFlexible: request.IsFlexible),
            GoalIds: request.GoalIds,
            Icon: request.Icon);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}/checklist")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateChecklist(
        Guid id,
        [FromBody] UpdateChecklistRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateChecklistCommand(
            HttpContext.GetUserId(),
            id,
            request.ChecklistItems);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteHabit(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteHabitCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogHabitDeleted(logger, id, HttpContext.GetUserId());
            return NoContent();
        }

        return BadRequest(new { error = result.Error });
    }

    [HttpGet("logs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllLogs(
        [FromQuery] DateOnly dateFrom,
        [FromQuery] DateOnly dateTo,
        CancellationToken cancellationToken)
    {
        var query = new GetAllHabitLogsQuery(HttpContext.GetUserId(), dateFrom, dateTo);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpGet("{id:guid}/logs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetLogs(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitLogsQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpGet("{id:guid}/metrics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMetrics(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitMetricsQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpPost("bulk")]
    [DistributedRateLimit("bulk")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> BulkCreate(
        [FromBody] BulkCreateHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var habits = request.Habits.Select(MapToBulkHabitItem).ToList();

        var command = new BulkCreateHabitsCommand(
            HttpContext.GetUserId(),
            habits,
            request.FromSyncReview);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogBulkCreated(logger, request.Habits.Count, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(v => StatusCode(StatusCodes.Status201Created, v));
    }

    [HttpDelete("bulk")]
    [DistributedRateLimit("bulk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> BulkDelete(
        [FromBody] BulkDeleteHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new BulkDeleteHabitsCommand(
            HttpContext.GetUserId(),
            request.HabitIds);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogBulkDeleted(logger, request.HabitIds.Count, HttpContext.GetUserId());

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("bulk/log")]
    [DistributedRateLimit("bulk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> BulkLog(
        [FromBody] BulkLogHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var items = request.Items.Select(i => new BulkLogItem(i.HabitId, i.Date)).ToList();
        var command = new BulkLogHabitsCommand(
            HttpContext.GetUserId(),
            items);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogBulkLogged(logger, request.Items.Count, HttpContext.GetUserId());

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("bulk/skip")]
    [DistributedRateLimit("bulk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> BulkSkip(
        [FromBody] BulkSkipHabitsRequest request,
        CancellationToken cancellationToken)
    {
        var items = request.Items.Select(i => new BulkSkipItem(i.HabitId, i.Date)).ToList();
        var command = new BulkSkipHabitsCommand(
            HttpContext.GetUserId(),
            items);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogBulkSkipped(logger, request.Items.Count, HttpContext.GetUserId());

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("reorder")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DuplicateHabit(
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = new DuplicateHabitCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetHabitById), new { id = result.Value }, new { id = result.Value })
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("{parentId:guid}/sub-habits")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateSubHabit(
        Guid parentId,
        [FromBody] CreateSubHabitRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateSubHabitCommand(
            HttpContext.GetUserId(),
            parentId,
            request.Title,
            request.Description,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            IsBadHabit: request.IsBadHabit,
            DueDate: request.DueDate,
            Options: new HabitCommandOptions(
                Days: request.Days,
                DueTime: request.DueTime,
                DueEndTime: request.DueEndTime,
                ReminderEnabled: request.ReminderEnabled,
                ReminderTimes: request.ReminderTimes,
                SlipAlertEnabled: request.SlipAlertEnabled,
                ChecklistItems: request.ChecklistItems,
                ScheduledReminders: request.ScheduledReminders,
                EndDate: request.EndDate,
                IsFlexible: request.IsFlexible),
            TagIds: request.TagIds,
            Icon: request.Icon);

        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(v => CreatedAtAction(nameof(GetHabitById), new { id = v }, new { id = v }));
    }

    [HttpPut("{habitId:guid}/goals")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LinkGoals(
        Guid habitId,
        [FromBody] LinkGoalsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new LinkGoalsToHabitCommand(HttpContext.GetUserId(), habitId, request.GoalIds);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogLinkedGoalsToHabit(logger, request.GoalIds.Count, habitId, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
    }

    private static BulkHabitItem MapToBulkHabitItem(BulkHabitItemRequest request)
    {
        return new BulkHabitItem(
            Title: request.Title,
            Description: request.Description,
            FrequencyUnit: request.FrequencyUnit,
            FrequencyQuantity: request.FrequencyQuantity,
            Days: request.Days,
            IsBadHabit: request.IsBadHabit,
            DueDate: request.DueDate,
            DueTime: request.DueTime,
            DueEndTime: request.DueEndTime,
            ReminderEnabled: request.ReminderEnabled,
            ReminderTimes: request.ReminderTimes,
            SubHabits: request.SubHabits?.Select(MapToBulkHabitItem).ToList(),
            IsGeneral: request.IsGeneral,
            EndDate: request.EndDate,
            IsFlexible: request.IsFlexible,
            ScheduledReminders: request.ScheduledReminders,
            ChecklistItems: request.ChecklistItems,
            GoogleEventId: request.GoogleEventId,
            Icon: request.Icon);
    }


    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Habit created {HabitId} by user {UserId}")]
    private static partial void LogHabitCreated(ILogger logger, Guid habitId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Habit deleted {HabitId} by user {UserId}")]
    private static partial void LogHabitDeleted(ILogger logger, Guid habitId, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Bulk created {Count} habits for user {UserId}")]
    private static partial void LogBulkCreated(ILogger logger, int count, Guid userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Bulk deleted {Count} habits for user {UserId}")]
    private static partial void LogBulkDeleted(ILogger logger, int count, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Bulk logged {Count} habits for user {UserId}")]
    private static partial void LogBulkLogged(ILogger logger, int count, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Bulk skipped {Count} habits for user {UserId}")]
    private static partial void LogBulkSkipped(ILogger logger, int count, Guid userId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Linked {Count} goals to habit {HabitId} by user {UserId}")]
    private static partial void LogLinkedGoalsToHabit(ILogger logger, int count, Guid habitId, Guid userId);

}
