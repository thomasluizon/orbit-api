using MediatR;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
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
    IReadOnlyList<string>? Tags = null,
    int? IntervalWeeks = null,
    IReadOnlyList<RelativeReminderTime>? RelativeReminders = null);

public record BulkCreateResult(IReadOnlyList<BulkCreateItemResult> Results)
{
    [JsonPropertyName("applied_count")]
    public int AppliedCount => Results.Count(item => item.Status == BulkItemStatus.Success);

    [JsonPropertyName("total_matched")]
    public int TotalMatched => Results.Count;

    [JsonPropertyName("skipped_count")]
    public int SkippedCount => TotalMatched - AppliedCount;

    [JsonPropertyName("partial")]
    public bool Partial => AppliedCount != TotalMatched;
}

public record BulkCreateItemResult(
    int Index,
    BulkItemStatus Status,
    Guid? HabitId = null,
    string? Title = null,
    string? Error = null,
    string? Field = null);

public enum BulkItemStatus { Success, Failed }

/// <summary>Groups the repositories a bulk habit create touches to keep the handler constructor small.</summary>
public record BulkCreateHabitsRepositories(
    IGenericRepository<Habit> Habits,
    IGenericRepository<GoogleCalendarSyncSuggestion> Suggestions,
    IGenericRepository<Tag> Tags);

public class BulkCreateHabitsCommandHandler(
    BulkCreateHabitsRepositories repos,
    IPayGateService payGate,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    IAppConfigService appConfigService) : IRequestHandler<BulkCreateHabitsCommand, Result<BulkCreateResult>>
{
    public async Task<Result<BulkCreateResult>> Handle(BulkCreateHabitsCommand request, CancellationToken cancellationToken)
    {
        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var result = await HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            request.UserId,
            ct => HandleLockedAsync(request, userToday, ct),
            cancellationToken);

        if (result.IsSuccess)
            CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, userToday);

        return result;
    }

    private async Task<Result<BulkCreateResult>> HandleLockedAsync(
        BulkCreateHabitsCommand request,
        DateOnly userToday,
        CancellationToken cancellationToken)
    {
        var parentCount = request.Habits.Count;
        var habitGate = await payGate.CanCreateHabits(request.UserId, parentCount, cancellationToken);
        if (habitGate.IsFailure)
            return habitGate.PropagateError<BulkCreateResult>();

        var results = new List<BulkCreateItemResult>();

        var existingRoots = await repos.Habits.FindAsync(
            h => h.UserId == request.UserId && h.ParentHabitId == null && !h.IsDeleted,
            cancellationToken);
        var rootPositionCursor = existingRoots.Count == 0
            ? 0
            : existingRoots.Max(h => h.Position ?? -1) + 1;

        var maxDepth = await appConfigService.GetAsync(
            AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, cancellationToken);
        var tagsByName = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        var tagsLoaded = false;
        var createdHabits = new List<Habit>();

        for (int i = 0; i < request.Habits.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = request.Habits[i];
            var subtree = BulkHabitSubtree.Build(
                request.UserId, item, userToday, rootPositionCursor + i, maxDepth, cancellationToken,
                out var field);
            if (subtree.IsFailure)
            {
                results.Add(new(i, BulkItemStatus.Failed, Title: item.Title, Error: subtree.Error, Field: field));
                continue;
            }

            if (!tagsLoaded && subtree.Value.Any(node => node.Tags.Count > 0))
            {
                tagsByName = await LoadTagCacheAsync(request.UserId, cancellationToken);
                tagsLoaded = true;
            }

            foreach (var node in subtree.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AttachTagsAsync(node, tagsByName, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await repos.Habits.AddAsync(node.Habit, cancellationToken);
                createdHabits.Add(node.Habit);
            }

            var root = subtree.Value[0].Habit;
            results.Add(new(i, BulkItemStatus.Success, root.Id, root.Title));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (request.FromSyncReview)
        {
            await MarkSyncSuggestionsImported(request.UserId, createdHabits, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(new BulkCreateResult(results));
    }

    internal static async Task<Result> CheckArgumentsAsync(
        BulkCreateHabitsCommand request, IUserDateService dates, IAppConfigService config, CancellationToken ct)
    {
        var today = await dates.GetUserTodayAsync(request.UserId, ct);
        var maxDepth = await config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, ct);
        foreach (var item in request.Habits)
        {
            var subtree = BulkHabitSubtree.Build(request.UserId, item, today, 0, maxDepth, ct, out _);
            if (subtree.IsFailure)
                return subtree;
        }
        return Result.Success();
    }

    private async Task<Dictionary<string, Tag>> LoadTagCacheAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var tagsByName = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        var existingTags = await repos.Tags.FindTrackedAsync(t => t.UserId == userId, cancellationToken);
        foreach (var tag in existingTags)
            tagsByName[tag.Name] = tag;

        return tagsByName;
    }

    private async Task AttachTagsAsync(
        BulkHabitSubtree.Node node, Dictionary<string, Tag> tagsByName, CancellationToken cancellationToken)
    {
        foreach (var candidate in node.Tags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tagsByName.TryGetValue(candidate.Name, out var tag))
            {
                tag = candidate;
                await repos.Tags.AddAsync(tag, cancellationToken);
                tagsByName[tag.Name] = tag;
            }

            if (tag.UserId != node.Habit.UserId)
                throw new InvalidOperationException(ErrorMessages.TagNotFound.Message);
            node.Habit.AddTag(tag);
        }
    }

    private async Task MarkSyncSuggestionsImported(
        Guid userId,
        IReadOnlyList<Habit> createdHabits,
        CancellationToken cancellationToken)
    {
        var habitsByEventId = createdHabits
            .Where(h => h.UserId == userId && h.GoogleEventId is not null)
            .ToDictionary(h => h.GoogleEventId!, h => h.Id);

        if (habitsByEventId.Count == 0)
            return;

        var eventIds = habitsByEventId.Keys.ToList();
        var suggestions = await repos.Suggestions.FindTrackedAsync(
            s => s.UserId == userId && eventIds.Contains(s.GoogleEventId) && s.ImportedAtUtc == null,
            cancellationToken);

#pragma warning disable ORBIT0004 // MarkImported records the same ImportedAtUtc audit instant for every matched calendar suggestion in this import. https://github.com/thomasluizon/orbit-tickets/issues/230
        var now = DateTime.UtcNow;
#pragma warning restore ORBIT0004
        foreach (var suggestion in suggestions)
        {
            if (habitsByEventId.TryGetValue(suggestion.GoogleEventId, out var habitId))
                suggestion.MarkImported(habitId, now);
        }
    }
}
