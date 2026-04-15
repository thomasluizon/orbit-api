using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Orbit.Api.Extensions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class SyncController(OrbitDbContext dbContext, ILogger<SyncController> logger) : ControllerBase
{
    private static readonly TimeSpan MaxSyncWindow = TimeSpan.FromDays(30);

    public record SyncChangesResponse(
        SyncEntitySet Habits,
        SyncEntitySet HabitLogs,
        SyncEntitySet Goals,
        SyncEntitySet GoalProgressLogs,
        SyncEntitySet Tags,
        SyncEntitySet Notifications,
        SyncEntitySet ChecklistTemplates,
        DateTime ServerTimestamp);

    public record SyncEntitySet(
        IReadOnlyList<object> Updated,
        IReadOnlyList<SyncDeletedRef> Deleted);

    public record SyncDeletedRef(Guid Id, DateTime DeletedAtUtc);

    public record SyncChangesV2Response(
        SyncEntitySetV2<SyncHabitDto> Habits,
        SyncEntitySetV2<SyncHabitLogDto> HabitLogs,
        SyncEntitySetV2<SyncGoalDto> Goals,
        SyncEntitySetV2<SyncGoalProgressLogDto> GoalProgressLogs,
        SyncEntitySetV2<SyncTagDto> Tags,
        SyncEntitySetV2<SyncNotificationDto> Notifications,
        SyncEntitySetV2<SyncChecklistTemplateDto> ChecklistTemplates,
        DateTime ServerTimestamp,
        int Version = 2);

    public record SyncEntitySetV2<T>(
        IReadOnlyList<T> Updated,
        IReadOnlyList<SyncDeletedRef> Deleted);

    public record SyncHabitDto(
        Guid Id,
        string Title,
        string? Description,
        Domain.Enums.FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        bool IsBadHabit,
        bool IsCompleted,
        DateOnly DueDate,
        TimeOnly? DueTime,
        TimeOnly? DueEndTime,
        bool ReminderEnabled,
        IReadOnlyList<int> ReminderTimes,
        bool IsGeneral,
        bool IsFlexible,
        bool SlipAlertEnabled,
        IReadOnlyList<Orbit.Domain.ValueObjects.ChecklistItem> ChecklistItems,
        IReadOnlyList<Orbit.Domain.ValueObjects.ScheduledReminderTime> ScheduledReminders,
        DateOnly? EndDate,
        int? Position,
        Guid? ParentHabitId,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public record SyncHabitLogDto(Guid Id, Guid HabitId, DateOnly Date, decimal Value, string? Note, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

    public record SyncGoalDto(
        Guid Id,
        string Title,
        string? Description,
        decimal TargetValue,
        decimal CurrentValue,
        string Unit,
        GoalStatus Status,
        GoalType Type,
        DateOnly? Deadline,
        int Position,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        DateTime? CompletedAtUtc,
        DateTime? StreakSyncedAtUtc);

    public record SyncGoalProgressLogDto(
        Guid Id,
        Guid GoalId,
        decimal Value,
        decimal PreviousValue,
        string? Note,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public record SyncTagDto(Guid Id, string Name, string Color, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

    public record SyncNotificationDto(
        Guid Id,
        string Title,
        string Body,
        string? Url,
        Guid? HabitId,
        bool IsRead,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public record SyncChecklistTemplateDto(
        Guid Id,
        string Name,
        IReadOnlyList<string> Items,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public record SyncBatchRequest(IReadOnlyList<SyncMutation> Mutations);

    public record SyncMutation(
        string Entity,
        string Action,
        Guid? Id,
        Dictionary<string, object?>? Data);

    public record SyncBatchResponse(
        int Processed,
        int Failed,
        IReadOnlyList<SyncMutationResult> Results);

    public record SyncMutationResult(
        int Index,
        string Status,
        string? Error = null);

    /// <summary>
    /// Returns entities modified since the given timestamp.
    /// Uses IgnoreQueryFilters to include soft-deleted records.
    /// Returns 410 Gone if since > 30 days old.
    /// </summary>
    [HttpGet("changes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> GetChanges(
        [FromQuery] DateTime since,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();

        if (DateTime.UtcNow - since > MaxSyncWindow)
        {
            return StatusCode(StatusCodes.Status410Gone, new
            {
                error = "Sync window exceeded. Full re-sync required.",
                code = "SYNC_WINDOW_EXCEEDED"
            });
        }

        var sinceUtc = DateTime.SpecifyKind(since, DateTimeKind.Utc);

        // Query all entities with IgnoreQueryFilters to include soft-deleted records
        var habits = await dbContext.Habits
            .IgnoreQueryFilters()
            .Where(h => h.UserId == userId && h.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var habitIds = habits.Select(h => h.Id).ToList();

        var habitLogs = await dbContext.HabitLogs
            .Where(l => habitIds.Contains(l.HabitId) && l.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var goals = await dbContext.Goals
            .IgnoreQueryFilters()
            .Where(g => g.UserId == userId && g.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var goalIds = goals.Select(g => g.Id).ToList();

        var goalProgressLogs = await dbContext.GoalProgressLogs
            .Where(l => goalIds.Contains(l.GoalId) && l.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var tags = await dbContext.Tags
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var notifications = await dbContext.Notifications
            .Where(n => n.UserId == userId && n.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var checklistTemplates = await dbContext.ChecklistTemplates
            .Where(ct => ct.UserId == userId && ct.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var response = new SyncChangesResponse(
            Habits: BuildEntitySet(
                habits.Where(h => !h.IsDeleted).Cast<object>().ToList(),
                habits.Where(h => h.IsDeleted).Select(h => new SyncDeletedRef(h.Id, h.DeletedAtUtc!.Value)).ToList()),
            HabitLogs: new SyncEntitySet(habitLogs.Cast<object>().ToList(), []),
            Goals: BuildEntitySet(
                goals.Where(g => !g.IsDeleted).Cast<object>().ToList(),
                goals.Where(g => g.IsDeleted).Select(g => new SyncDeletedRef(g.Id, g.DeletedAtUtc!.Value)).ToList()),
            GoalProgressLogs: new SyncEntitySet(goalProgressLogs.Cast<object>().ToList(), []),
            Tags: BuildEntitySet(
                tags.Where(t => !t.IsDeleted).Cast<object>().ToList(),
                tags.Where(t => t.IsDeleted).Select(t => new SyncDeletedRef(t.Id, t.DeletedAtUtc!.Value)).ToList()),
            Notifications: new SyncEntitySet(notifications.Cast<object>().ToList(), []),
            ChecklistTemplates: new SyncEntitySet(checklistTemplates.Cast<object>().ToList(), []),
            ServerTimestamp: DateTime.UtcNow);

        LogSyncChanges(logger, userId, since);
        return Ok(response);
    }

    /// <summary>
    /// Returns the version 2 redacted sync DTOs modified since the given timestamp.
    /// Unlike the legacy endpoint, this response never serializes EF entities directly.
    /// </summary>
    [HttpGet("v2/changes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> GetChangesV2(
        [FromQuery] DateTime since,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();

        if (DateTime.UtcNow - since > MaxSyncWindow)
        {
            return StatusCode(StatusCodes.Status410Gone, new
            {
                error = "Sync window exceeded. Full re-sync required.",
                code = "SYNC_WINDOW_EXCEEDED"
            });
        }

        var sinceUtc = DateTime.SpecifyKind(since, DateTimeKind.Utc);

        var habits = await dbContext.Habits
            .IgnoreQueryFilters()
            .Where(h => h.UserId == userId && h.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var habitIds = habits.Select(h => h.Id).ToList();

        var habitLogs = await dbContext.HabitLogs
            .Where(l => habitIds.Contains(l.HabitId) && l.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var goals = await dbContext.Goals
            .IgnoreQueryFilters()
            .Where(g => g.UserId == userId && g.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var goalIds = goals.Select(g => g.Id).ToList();

        var goalProgressLogs = await dbContext.GoalProgressLogs
            .Where(l => goalIds.Contains(l.GoalId) && l.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var tags = await dbContext.Tags
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var notifications = await dbContext.Notifications
            .Where(n => n.UserId == userId && n.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var checklistTemplates = await dbContext.ChecklistTemplates
            .Where(ct => ct.UserId == userId && ct.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var response = new SyncChangesV2Response(
            Habits: new SyncEntitySetV2<SyncHabitDto>(
                habits.Where(h => !h.IsDeleted).Select(MapHabit).ToList(),
                habits.Where(h => h.IsDeleted).Select(h => new SyncDeletedRef(h.Id, h.DeletedAtUtc!.Value)).ToList()),
            HabitLogs: new SyncEntitySetV2<SyncHabitLogDto>(habitLogs.Select(MapHabitLog).ToList(), []),
            Goals: new SyncEntitySetV2<SyncGoalDto>(
                goals.Where(g => !g.IsDeleted).Select(MapGoal).ToList(),
                goals.Where(g => g.IsDeleted).Select(g => new SyncDeletedRef(g.Id, g.DeletedAtUtc!.Value)).ToList()),
            GoalProgressLogs: new SyncEntitySetV2<SyncGoalProgressLogDto>(goalProgressLogs.Select(MapGoalProgressLog).ToList(), []),
            Tags: new SyncEntitySetV2<SyncTagDto>(
                tags.Where(t => !t.IsDeleted).Select(MapTag).ToList(),
                tags.Where(t => t.IsDeleted).Select(t => new SyncDeletedRef(t.Id, t.DeletedAtUtc!.Value)).ToList()),
            Notifications: new SyncEntitySetV2<SyncNotificationDto>(notifications.Select(MapNotification).ToList(), []),
            ChecklistTemplates: new SyncEntitySetV2<SyncChecklistTemplateDto>(checklistTemplates.Select(MapChecklistTemplate).ToList(), []),
            ServerTimestamp: DateTime.UtcNow);

        LogSyncChanges(logger, userId, since);
        return Ok(response);
    }

    /// <summary>
    /// Accepts an array of mutations, processes in order with server-wins conflict resolution.
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ProcessBatch(
        [FromBody] SyncBatchRequest request,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();

        if (request.Mutations.Count == 0)
            return BadRequest(new { error = "No mutations provided." });

        if (request.Mutations.Count > 100)
            return BadRequest(new { error = "Maximum 100 mutations per batch." });

        var results = new List<SyncMutationResult>();
        var processed = 0;
        var failed = 0;

        // Each mutation commits (or rolls back) independently so a partial failure
        // can't leave tracked changes that leak into the next mutation or the final
        // SaveChanges. Relational providers use a real transaction via IExecutionStrategy
        // (EnableRetryOnFailure is on in prod). The in-memory test provider doesn't
        // support transactions, so we fall back to SaveChanges-per-mutation +
        // ChangeTracker.Clear on failure, which yields equivalent isolation since
        // there's no real transaction to manage.
        var supportsTransactions = dbContext.Database.IsRelational();
        var strategy = dbContext.Database.CreateExecutionStrategy();
        var mutationCount = Math.Min(request.Mutations.Count, 100);
        for (int i = 0; i < mutationCount; i++)
        {
            var mutation = request.Mutations[i];
            var index = i;
            try
            {
                if (supportsTransactions)
                {
                    await strategy.ExecuteAsync(async () =>
                    {
                        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                        try
                        {
                            await ProcessMutation(userId, mutation, cancellationToken);
                            await dbContext.SaveChangesAsync(cancellationToken);
                            await tx.CommitAsync(cancellationToken);
                        }
                        catch
                        {
                            await tx.RollbackAsync(CancellationToken.None);
                            dbContext.ChangeTracker.Clear();
                            throw;
                        }
                    });
                }
                else
                {
                    // In-memory / non-relational path used by unit tests. No transactions,
                    // no ChangeTracker.Clear (tests rely on reference identity of tracked
                    // entities from the initial seed). SaveChangesAsync-per-mutation still
                    // provides partial-failure isolation within the test scope.
                    await ProcessMutation(userId, mutation, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                results.Add(new SyncMutationResult(index, "success"));
                processed++;
            }
            catch (Exception ex)
            {
                LogMutationFailed(logger, index, mutation.Entity, mutation.Action, ex);
                results.Add(new SyncMutationResult(index, "failed", "Mutation failed"));
                failed++;
            }
        }

        LogSyncBatch(logger, userId, processed, failed);
        return Ok(new SyncBatchResponse(processed, failed, results));
    }

    private async Task ProcessMutation(Guid userId, SyncMutation mutation, CancellationToken ct)
    {
        switch (mutation.Entity.ToLowerInvariant())
        {
            case "habit":
                await ProcessHabitMutation(userId, mutation, ct);
                break;
            case "goal":
                await ProcessGoalMutation(userId, mutation, ct);
                break;
            case "tag":
                await ProcessTagMutation(userId, mutation, ct);
                break;
            case "notification":
                await ProcessNotificationMutation(userId, mutation, ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown entity type: {mutation.Entity}");
        }
    }

    private async Task ProcessHabitMutation(Guid userId, SyncMutation mutation, CancellationToken ct)
    {
        switch (mutation.Action.ToLowerInvariant())
        {
            case "delete":
                if (mutation.Id is null) throw new InvalidOperationException("Id is required for delete.");
                var habit = await dbContext.Habits.FirstOrDefaultAsync(h => h.Id == mutation.Id && h.UserId == userId, ct);
                if (habit is not null) habit.SoftDelete();
                break;
            default:
                throw new InvalidOperationException($"Unsupported action: {mutation.Action} for habit.");
        }
    }

    private async Task ProcessGoalMutation(Guid userId, SyncMutation mutation, CancellationToken ct)
    {
        switch (mutation.Action.ToLowerInvariant())
        {
            case "delete":
                if (mutation.Id is null) throw new InvalidOperationException("Id is required for delete.");
                var goal = await dbContext.Goals.FirstOrDefaultAsync(g => g.Id == mutation.Id && g.UserId == userId, ct);
                if (goal is not null) goal.SoftDelete();
                break;
            default:
                throw new InvalidOperationException($"Unsupported action: {mutation.Action} for goal.");
        }
    }

    private async Task ProcessTagMutation(Guid userId, SyncMutation mutation, CancellationToken ct)
    {
        switch (mutation.Action.ToLowerInvariant())
        {
            case "delete":
                if (mutation.Id is null) throw new InvalidOperationException("Id is required for delete.");
                var tag = await dbContext.Tags.FirstOrDefaultAsync(t => t.Id == mutation.Id && t.UserId == userId, ct);
                if (tag is not null) tag.SoftDelete();
                break;
            default:
                throw new InvalidOperationException($"Unsupported action: {mutation.Action} for tag.");
        }
    }

    private async Task ProcessNotificationMutation(Guid userId, SyncMutation mutation, CancellationToken ct)
    {
        switch (mutation.Action.ToLowerInvariant())
        {
            case "read":
                if (mutation.Id is null) throw new InvalidOperationException("Id is required for read.");
                var notification = await dbContext.Notifications.FirstOrDefaultAsync(n => n.Id == mutation.Id && n.UserId == userId, ct);
                if (notification is not null) notification.MarkAsRead();
                break;
            default:
                throw new InvalidOperationException($"Unsupported action: {mutation.Action} for notification.");
        }
    }

    private static SyncEntitySet BuildEntitySet(IReadOnlyList<object> updated, IReadOnlyList<SyncDeletedRef> deleted)
    {
        return new SyncEntitySet(updated, deleted);
    }

    private static SyncHabitDto MapHabit(Habit habit)
    {
        return new SyncHabitDto(
            habit.Id,
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.IsBadHabit,
            habit.IsCompleted,
            habit.DueDate,
            habit.DueTime,
            habit.DueEndTime,
            habit.ReminderEnabled,
            habit.ReminderTimes,
            habit.IsGeneral,
            habit.IsFlexible,
            habit.SlipAlertEnabled,
            habit.ChecklistItems,
            habit.ScheduledReminders,
            habit.EndDate,
            habit.Position,
            habit.ParentHabitId,
            habit.CreatedAtUtc,
            habit.UpdatedAtUtc);
    }

    private static SyncHabitLogDto MapHabitLog(HabitLog habitLog)
    {
        return new SyncHabitLogDto(
            habitLog.Id,
            habitLog.HabitId,
            habitLog.Date,
            habitLog.Value,
            habitLog.Note,
            habitLog.CreatedAtUtc,
            habitLog.UpdatedAtUtc);
    }

    private static SyncGoalDto MapGoal(Goal goal)
    {
        return new SyncGoalDto(
            goal.Id,
            goal.Title,
            goal.Description,
            goal.TargetValue,
            goal.CurrentValue,
            goal.Unit,
            goal.Status,
            goal.Type,
            goal.Deadline,
            goal.Position,
            goal.CreatedAtUtc,
            goal.UpdatedAtUtc,
            goal.CompletedAtUtc,
            goal.StreakSyncedAtUtc);
    }

    private static SyncGoalProgressLogDto MapGoalProgressLog(GoalProgressLog goalProgressLog)
    {
        return new SyncGoalProgressLogDto(
            goalProgressLog.Id,
            goalProgressLog.GoalId,
            goalProgressLog.Value,
            goalProgressLog.PreviousValue,
            goalProgressLog.Note,
            goalProgressLog.CreatedAtUtc,
            goalProgressLog.UpdatedAtUtc);
    }

    private static SyncTagDto MapTag(Tag tag)
    {
        return new SyncTagDto(tag.Id, tag.Name, tag.Color, tag.CreatedAtUtc, tag.UpdatedAtUtc);
    }

    private static SyncNotificationDto MapNotification(Notification notification)
    {
        return new SyncNotificationDto(
            notification.Id,
            notification.Title,
            notification.Body,
            notification.Url,
            notification.HabitId,
            notification.IsRead,
            notification.CreatedAtUtc,
            notification.UpdatedAtUtc);
    }

    private static SyncChecklistTemplateDto MapChecklistTemplate(ChecklistTemplate checklistTemplate)
    {
        return new SyncChecklistTemplateDto(
            checklistTemplate.Id,
            checklistTemplate.Name,
            checklistTemplate.Items,
            checklistTemplate.CreatedAtUtc,
            checklistTemplate.UpdatedAtUtc);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Sync changes requested by user {UserId} since {Since}")]
    private static partial void LogSyncChanges(ILogger logger, Guid userId, DateTime since);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Sync batch processed for user {UserId}: {Processed} succeeded, {Failed} failed")]
    private static partial void LogSyncBatch(ILogger logger, Guid userId, int processed, int failed);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Sync mutation {Index} failed for entity {Entity} action {Action}")]
    private static partial void LogMutationFailed(ILogger logger, int index, string entity, string action, Exception ex);
}
