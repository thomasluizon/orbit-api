using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Interfaces;

#pragma warning disable CA1873

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class HabitsController(IMediator mediator, ILogger<HabitsController> logger, IUserDateService userDateService) : ControllerBase
{
    [HttpGet("count")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetHabitCount(CancellationToken cancellationToken = default)
    {
        var query = new GetHabitCountQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("widget")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetHabitWidget(CancellationToken cancellationToken = default)
    {
        var query = new GetHabitWidgetQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetHabits(
        [FromQuery] GetHabitsFilterRequest filter,
        CancellationToken cancellationToken = default)
    {
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
            filter.Page,
            filter.PageSize,
            filter.IncludeGeneral ?? false);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

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
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("trends")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTrends(
        [FromQuery] DateOnly dateFrom,
        [FromQuery] DateOnly dateTo,
        CancellationToken cancellationToken = default)
    {
        var query = new GetHabitsCompletionTrendsQuery(HttpContext.GetUserId(), dateFrom, dateTo);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDailySummary(
        [FromQuery] DateOnly dateFrom,
        [FromQuery] DateOnly dateTo,
        [FromQuery] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var query = new GetDailySummaryQuery(
            HttpContext.GetUserId(),
            dateFrom,
            dateTo,
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
        if (!RetrospectivePeriodRange.IsKnownPeriod(period))
            return BadRequest(ErrorMessages.InvalidPeriod.ToErrorBody());

        var userId = HttpContext.GetUserId();
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(userId, cancellationToken);
        var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve(period, today, weekStartDay);

        var query = new GetRetrospectiveQuery(
            userId,
            dateFrom,
            dateTo,
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
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("{id:guid}/detail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHabitDetail(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitFullDetailQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("{id:guid}/reschedule-suggestion")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRescheduleSuggestion(
        Guid id,
        [FromQuery] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var query = new GetRescheduleSuggestionQuery(HttpContext.GetUserId(), id, language);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
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
            Emoji: request.Emoji);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogHabitCreated(logger, result.Value, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(v => CreatedAtAction(nameof(GetHabits), new { id = v }, new { id = v }));
    }

    [HttpPost("suggest-setup")]
    [DistributedRateLimit("habit-suggest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SuggestSetup(
        [FromBody] SuggestHabitSetupRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SuggestHabitSetupCommand(
            HttpContext.GetUserId(),
            request.Title,
            request.Language);

        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
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
            request?.Date);

        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(v => Ok(v));
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
            Emoji: request.Emoji);

        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(() => NoContent());
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

        return result.ToPayGateAwareResult(() => NoContent());
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

        return result.ToErrorResult();
    }

    [HttpPost("{id:guid}/restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RestoreHabit(Guid id, CancellationToken cancellationToken)
    {
        var command = new RestoreHabitCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() =>
        {
            LogHabitRestored(logger, id, HttpContext.GetUserId());
            return NoContent();
        });
    }

    [HttpGet("{id:guid}/logs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetLogs(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitLogsQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("{id:guid}/metrics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMetrics(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetHabitMetricsQuery(HttpContext.GetUserId(), id);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpPost("bulk")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpPost("bulk/log")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpPost("bulk/skip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

        return result.ToPayGateAwareResult(v => Ok(v));
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

        return result.ToPayGateAwareResult(() => NoContent());
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

        return result.ToPayGateAwareResult(() => NoContent());
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

        return result.ToPayGateAwareResult(
            v => CreatedAtAction(nameof(GetHabitById), new { id = v }, new { id = v }));
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
            Emoji: request.Emoji);

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
            Emoji: request.Emoji,
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
            Tags: request.Tags);
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

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Habit restored {HabitId} by user {UserId}")]
    private static partial void LogHabitRestored(ILogger logger, Guid habitId, Guid userId);

}
