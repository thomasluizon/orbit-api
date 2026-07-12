using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Commands;

public record BulkCreateHabitsCommand(
    Guid UserId,
    IReadOnlyList<BulkHabitItem> Habits,
    bool FromSyncReview = false) : IRequest<Result<BulkCreateResult>>;

public record BulkHabitItem(
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    IReadOnlyList<BulkHabitItem>? SubHabits = null,
    bool IsGeneral = false,
    DateOnly? EndDate = null,
    bool IsFlexible = false,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    string? GoogleEventId = null,
    string? Emoji = null,
    IReadOnlyList<string>? Tags = null);

public record BulkCreateResult(IReadOnlyList<BulkCreateItemResult> Results);

public record BulkCreateItemResult(
    int Index,
    BulkItemStatus Status,
    Guid? HabitId = null,
    string? Title = null,
    string? Error = null,
    string? Field = null);

public enum BulkItemStatus { Success, Failed }

public partial class BulkCreateHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<GoogleCalendarSyncSuggestion> suggestionRepository,
    IGenericRepository<Tag> tagRepository,
    IPayGateService payGate,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkCreateHabitsCommandHandler> logger) : IRequestHandler<BulkCreateHabitsCommand, Result<BulkCreateResult>>
{
    private const string DefaultTagColor = "#7c3aed";

    public async Task<Result<BulkCreateResult>> Handle(BulkCreateHabitsCommand request, CancellationToken cancellationToken)
    {
        var parentCount = request.Habits.Count;
        var habitGate = await payGate.CanCreateHabits(request.UserId, parentCount, cancellationToken);
        if (habitGate.IsFailure)
            return habitGate.PropagateError<BulkCreateResult>();

        var hasSubHabits = request.Habits.Any(h => h.SubHabits is { Count: > 0 });
        if (hasSubHabits)
        {
            var subGate = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
            if (subGate.IsFailure)
                return subGate.PropagateError<BulkCreateResult>();
        }

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var results = new List<BulkCreateItemResult>();

        var existingRoots = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.ParentHabitId == null && !h.IsDeleted,
            cancellationToken);
        var rootPositionCursor = existingRoots.Count == 0
            ? 0
            : existingRoots.Max(h => h.Position ?? -1) + 1;

        var tagsByName = await LoadTagCacheAsync(request, cancellationToken);

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            for (int i = 0; i < request.Habits.Count; i++)
            {
                var itemResult = await CreateSingleHabit(
                    request.UserId, request.Habits[i], i, userToday, rootPositionCursor + i, tagsByName, ct);
                results.Add(itemResult);
            }

            await unitOfWork.SaveChangesAsync(ct);

            if (request.FromSyncReview)
            {
                await MarkSyncSuggestionsImported(request.UserId, results, ct);
                await unitOfWork.SaveChangesAsync(ct);
            }
        }, cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId);

        return Result.Success(new BulkCreateResult(results));
    }

    private async Task<BulkCreateItemResult> CreateSingleHabit(
        Guid userId, BulkHabitItem item, int index, DateOnly userToday, int rootPosition,
        Dictionary<string, Tag> tagsByName, CancellationToken cancellationToken)
    {
        try
        {
            var habitResult = Habit.Create(new HabitCreateParams(
                userId,
                item.Title,
                item.FrequencyUnit,
                item.FrequencyQuantity,
                item.DueDate ?? userToday,
                item.Description,
                Emoji: item.Emoji,
                Days: item.Days,
                IsBadHabit: item.IsBadHabit,
                DueTime: item.DueTime,
                DueEndTime: item.DueEndTime,
                ReminderEnabled: item.ReminderEnabled,
                ReminderTimes: item.ReminderTimes,
                IsGeneral: item.IsGeneral,
                IsFlexible: item.IsFlexible,
                ScheduledReminders: item.ScheduledReminders,
                ChecklistItems: item.ChecklistItems,
                Position: rootPosition,
                GoogleEventId: item.GoogleEventId));

            if (habitResult.IsFailure)
            {
                return new BulkCreateItemResult(
                    Index: index,
                    Status: BulkItemStatus.Failed,
                    Title: item.Title,
                    Error: habitResult.Error,
                    Field: DetermineFieldFromError(habitResult.Error));
            }

            var parentHabit = habitResult.Value;
            await habitRepository.AddAsync(parentHabit, cancellationToken);

            await AttachTagsAsync(parentHabit, userId, item.Tags, tagsByName, cancellationToken);

            if (item.SubHabits is { Count: > 0 })
            {
                var subPositionCursor = 0;
                foreach (var subItem in item.SubHabits)
                {
                    var childResult = Habit.Create(new HabitCreateParams(
                        userId,
                        subItem.Title,
                        subItem.FrequencyUnit ?? item.FrequencyUnit,
                        subItem.FrequencyQuantity ?? item.FrequencyQuantity,
                        subItem.DueDate ?? item.DueDate ?? userToday,
                        subItem.Description,
                        Emoji: subItem.Emoji,
                        Days: subItem.Days ?? item.Days,
                        IsBadHabit: subItem.IsBadHabit,
                        ParentHabitId: parentHabit.Id,
                        IsGeneral: item.IsGeneral,
                        IsFlexible: subItem.IsFlexible,
                        Position: subPositionCursor++));

                    if (childResult.IsFailure)
                    {
                        habitRepository.Remove(parentHabit);
                        return new BulkCreateItemResult(
                            Index: index,
                            Status: BulkItemStatus.Failed,
                            Title: item.Title,
                            Error: $"Sub-habit '{subItem.Title}' failed: {childResult.Error}",
                            Field: "SubHabits");
                    }

                    await habitRepository.AddAsync(childResult.Value, cancellationToken);
                }
            }

            return new BulkCreateItemResult(
                Index: index,
                Status: BulkItemStatus.Success,
                HabitId: parentHabit.Id,
                Title: parentHabit.Title);
        }
        catch (Exception ex)
        {
            LogBulkCreateItemFailed(logger, ex, index);
            return new BulkCreateItemResult(
                Index: index,
                Status: BulkItemStatus.Failed,
                Title: item.Title,
                Error: "An error occurred processing this item");
        }
    }

    private async Task<Dictionary<string, Tag>> LoadTagCacheAsync(
        BulkCreateHabitsCommand request, CancellationToken cancellationToken)
    {
        var tagsByName = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        if (!request.Habits.Any(h => h.Tags is { Count: > 0 }))
            return tagsByName;

        var existingTags = await tagRepository.FindTrackedAsync(t => t.UserId == request.UserId, cancellationToken);
        foreach (var tag in existingTags)
            tagsByName[tag.Name] = tag;

        return tagsByName;
    }

    private async Task AttachTagsAsync(
        Habit habit, Guid userId, IReadOnlyList<string>? tagNames,
        Dictionary<string, Tag> tagsByName, CancellationToken cancellationToken)
    {
        if (tagNames is not { Count: > 0 })
            return;

        foreach (var rawName in tagNames)
        {
            var trimmed = rawName.Trim();
            if (trimmed.Length == 0)
                continue;

            if (!tagsByName.TryGetValue(trimmed, out var tag))
            {
                var created = Tag.Create(userId, trimmed, DefaultTagColor);
                if (created.IsFailure)
                    continue;

                tag = created.Value;
                await tagRepository.AddAsync(tag, cancellationToken);
                tagsByName[tag.Name] = tag;
            }

            habit.AddTag(tag);
        }
    }

    private static string? DetermineFieldFromError(string error)
    {
        if (error.Contains("title", StringComparison.OrdinalIgnoreCase))
            return "Title";
        if (error.Contains("frequency", StringComparison.OrdinalIgnoreCase))
            return "FrequencyUnit";
        if (error.Contains("days", StringComparison.OrdinalIgnoreCase))
            return "Days";
        return null;
    }

    private async Task MarkSyncSuggestionsImported(
        Guid userId,
        List<BulkCreateItemResult> results,
        CancellationToken cancellationToken)
    {
        var createdHabitIds = results
            .Where(r => r.Status == BulkItemStatus.Success && r.HabitId is not null)
            .Select(r => r.HabitId!.Value)
            .ToList();

        if (createdHabitIds.Count == 0)
            return;

        var createdHabits = await habitRepository.FindAsync(
            h => createdHabitIds.Contains(h.Id) && h.GoogleEventId != null,
            cancellationToken);

        var habitsByEventId = createdHabits
            .Where(h => h.GoogleEventId is not null)
            .ToDictionary(h => h.GoogleEventId!, h => h.Id);

        if (habitsByEventId.Count == 0)
            return;

        var eventIds = habitsByEventId.Keys.ToList();
        var suggestions = await suggestionRepository.FindAsync(
            s => s.UserId == userId && eventIds.Contains(s.GoogleEventId) && s.ImportedAtUtc == null,
            cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var suggestion in suggestions)
        {
            if (habitsByEventId.TryGetValue(suggestion.GoogleEventId, out var habitId))
                suggestion.MarkImported(habitId, now);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "BulkCreate item {Index} failed")]
    private static partial void LogBulkCreateItemFailed(ILogger logger, Exception ex, int index);
}
