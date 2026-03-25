using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Queries;

public record GeneralHabitItem(
    Guid Id,
    string Title,
    string? Description,
    bool IsBadHabit,
    bool IsCompleted,
    int? Position,
    DateTime CreatedAtUtc,
    TimeOnly? DueTime,
    bool ReminderEnabled,
    IReadOnlyList<int> ReminderTimes,
    bool SlipAlertEnabled,
    IReadOnlyList<ChecklistItem> ChecklistItems,
    IReadOnlyList<HabitTagItem> Tags,
    IReadOnlyList<GeneralHabitChildItem> Children,
    bool HasSubHabits);

public record GeneralHabitChildItem(
    Guid Id,
    string Title,
    string? Description,
    bool IsBadHabit,
    bool IsCompleted,
    int? Position,
    TimeOnly? DueTime,
    IReadOnlyList<ChecklistItem> ChecklistItems,
    IReadOnlyList<HabitTagItem> Tags,
    IReadOnlyList<GeneralHabitChildItem> Children);

public record GetGeneralHabitsQuery(
    Guid UserId,
    string? Search = null,
    bool? IsCompleted = null,
    IReadOnlyList<Guid>? TagIds = null,
    int Page = 1,
    int PageSize = 50) : IRequest<PaginatedResponse<GeneralHabitItem>>;

public class GetGeneralHabitsQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetGeneralHabitsQuery, PaginatedResponse<GeneralHabitItem>>
{
    public async Task<PaginatedResponse<GeneralHabitItem>> Handle(GetGeneralHabitsQuery request, CancellationToken cancellationToken)
    {
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsGeneral,
            q => q.Include(h => h.Tags),
            cancellationToken);

        var lookup = allHabits.ToLookup(h => h.ParentHabitId);

        IEnumerable<Habit> topLevel = lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            topLevel = topLevel.Where(h =>
                h.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (h.Description != null && h.Description.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        if (request.IsCompleted.HasValue)
            topLevel = topLevel.Where(h => h.IsCompleted == request.IsCompleted.Value);

        if (request.TagIds is { Count: > 0 })
        {
            var tagIdSet = request.TagIds.ToHashSet();
            bool HasMatchingTag(Habit h) => h.Tags.Any(t => tagIdSet.Contains(t.Id));
            bool HasDescendantWithTag(Guid parentId)
            {
                foreach (var child in lookup[parentId])
                {
                    if (HasMatchingTag(child)) return true;
                    if (HasDescendantWithTag(child.Id)) return true;
                }
                return false;
            }
            topLevel = topLevel.Where(h => HasMatchingTag(h) || HasDescendantWithTag(h.Id));
        }

        var filtered = topLevel.ToList();

        var totalCount = filtered.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);
        var page = Math.Max(1, Math.Min(request.Page, Math.Max(1, totalPages)));

        var pagedItems = filtered
            .Skip((page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(h => MapToGeneralItem(h, lookup))
            .ToList();

        return new PaginatedResponse<GeneralHabitItem>(
            pagedItems,
            page,
            request.PageSize,
            totalCount,
            totalPages);
    }

    private static GeneralHabitItem MapToGeneralItem(Habit h, ILookup<Guid?, Habit> lookup) => new(
        h.Id,
        h.Title,
        h.Description,
        h.IsBadHabit,
        h.IsCompleted,
        h.Position,
        h.CreatedAtUtc,
        h.DueTime,
        h.ReminderEnabled,
        h.ReminderTimes,
        h.SlipAlertEnabled,
        h.ChecklistItems,
        MapTags(h),
        MapChildren(h.Id, lookup),
        lookup[h.Id].Any());

    private static List<GeneralHabitChildItem> MapChildren(Guid parentId, ILookup<Guid?, Habit> lookup) =>
        lookup[parentId]
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => new GeneralHabitChildItem(
                c.Id, c.Title, c.Description,
                c.IsBadHabit, c.IsCompleted,
                c.Position, c.DueTime,
                c.ChecklistItems, MapTags(c), MapChildren(c.Id, lookup)))
            .ToList();

    private static List<HabitTagItem> MapTags(Habit h) =>
        h.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color)).ToList();
}
