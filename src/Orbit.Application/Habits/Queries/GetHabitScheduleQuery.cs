using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record HabitTagItem(Guid Id, string Name, string Color);

public record HabitScheduleItem(
    Guid Id,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit,
    bool IsCompleted,
    IReadOnlyList<DayOfWeek> Days,
    int? Position,
    DateTime CreatedAtUtc,
    DateOnly DueDate,
    TimeOnly? DueTime,
    IReadOnlyList<DateOnly> ScheduledDates,
    bool IsOverdue,
    IReadOnlyList<HabitTagItem> Tags,
    IReadOnlyList<HabitScheduleChildItem> Children);

public record HabitScheduleChildItem(
    Guid Id,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit,
    bool IsCompleted,
    IReadOnlyList<DayOfWeek> Days,
    DateOnly DueDate,
    TimeOnly? DueTime,
    int? Position,
    IReadOnlyList<HabitTagItem> Tags,
    IReadOnlyList<HabitScheduleChildItem> Children);

public record GetHabitScheduleQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    bool IncludeOverdue = false,
    string? Search = null,
    string? FrequencyUnitFilter = null,
    bool? IsCompleted = null,
    IReadOnlyList<Guid>? TagIds = null,
    int Page = 1,
    int PageSize = 50) : IRequest<PaginatedResponse<HabitScheduleItem>>;

public class GetHabitScheduleQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitScheduleQuery, PaginatedResponse<HabitScheduleItem>>
{
    public async Task<PaginatedResponse<HabitScheduleItem>> Handle(GetHabitScheduleQuery request, CancellationToken cancellationToken)
    {
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsActive,
            q => q.Include(h => h.Tags),
            cancellationToken);

        var lookup = allHabits.ToLookup(h => h.ParentHabitId);

        // Start with top-level habits
        IEnumerable<Habit> topLevel = lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc);

        // Text search filter
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            topLevel = topLevel.Where(h =>
                h.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (h.Description != null && h.Description.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        // Frequency unit filter
        if (!string.IsNullOrWhiteSpace(request.FrequencyUnitFilter))
        {
            if (request.FrequencyUnitFilter.Equals("none", StringComparison.OrdinalIgnoreCase))
                topLevel = topLevel.Where(h => h.FrequencyUnit == null);
            else if (Enum.TryParse<FrequencyUnit>(request.FrequencyUnitFilter, true, out var unit))
                topLevel = topLevel.Where(h => h.FrequencyUnit == unit);
        }

        // Completion filter
        if (request.IsCompleted.HasValue)
            topLevel = topLevel.Where(h => h.IsCompleted == request.IsCompleted.Value);

        // Tag filter: include habits that have ANY of the requested tags,
        // or have a descendant with any of the requested tags
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

        // Schedule-aware filtering: keep habits that have at least one scheduled date in range,
        // OR are overdue, OR have any descendant due in range
        var filtered = new List<(Habit habit, List<DateOnly> scheduledDates, bool isOverdue)>();

        foreach (var habit in topLevel)
        {
            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, request.DateFrom, request.DateTo);
            var isOverdue = false;

            if (request.IncludeOverdue && !habit.IsCompleted && habit.DueDate < request.DateFrom)
            {
                isOverdue = true;
            }

            var hasDescendantDue = HasAnyDescendantDue(habit.Id, lookup, request.DateFrom, request.DateTo);

            if (scheduledDates.Count > 0 || isOverdue || hasDescendantDue)
            {
                filtered.Add((habit, scheduledDates, isOverdue));
            }
        }

        // Pagination
        var totalCount = filtered.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);
        var page = Math.Max(1, Math.Min(request.Page, Math.Max(1, totalPages)));

        var pagedItems = filtered
            .Skip((page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => MapToScheduleItem(x.habit, x.scheduledDates, x.isOverdue, lookup, request.DateFrom, request.DateTo))
            .ToList();

        return new PaginatedResponse<HabitScheduleItem>(
            pagedItems,
            page,
            request.PageSize,
            totalCount,
            totalPages);
    }

    private static HabitScheduleItem MapToScheduleItem(
        Habit h,
        List<DateOnly> scheduledDates,
        bool isOverdue,
        ILookup<Guid?, Habit> lookup,
        DateOnly dateFrom,
        DateOnly dateTo) => new(
            h.Id,
            h.Title,
            h.Description,
            h.FrequencyUnit,
            h.FrequencyQuantity,
            h.IsBadHabit,
            h.IsCompleted,
            h.Days.ToList(),
            h.Position,
            h.CreatedAtUtc,
            h.DueDate,
            h.DueTime,
            scheduledDates,
            isOverdue,
            MapTags(h),
            MapChildren(h.Id, lookup, dateFrom, dateTo));

    private static bool HasAnyDescendantDue(Guid parentId, ILookup<Guid?, Habit> lookup, DateOnly dateFrom, DateOnly dateTo)
    {
        foreach (var child in lookup[parentId])
        {
            if (HabitScheduleService.GetScheduledDates(child, dateFrom, dateTo).Count > 0)
                return true;
            if (!child.IsCompleted && child.DueDate < dateFrom)
                return true;
            if (HasAnyDescendantDue(child.Id, lookup, dateFrom, dateTo))
                return true;
        }
        return false;
    }

    private static List<HabitScheduleChildItem> MapChildren(Guid parentId, ILookup<Guid?, Habit> lookup, DateOnly dateFrom, DateOnly dateTo) =>
        lookup[parentId]
            .Where(c => HabitScheduleService.GetScheduledDates(c, dateFrom, dateTo).Count > 0
                || c.IsCompleted
                || (!c.IsCompleted && c.DueDate < dateFrom)
                || HasAnyDescendantDue(c.Id, lookup, dateFrom, dateTo))
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => new HabitScheduleChildItem(
                c.Id, c.Title, c.Description,
                c.FrequencyUnit, c.FrequencyQuantity, c.IsBadHabit, c.IsCompleted,
                c.Days.ToList(), c.DueDate, c.DueTime,
                c.Position, MapTags(c), MapChildren(c.Id, lookup, dateFrom, dateTo)))
            .ToList();

    private static List<HabitTagItem> MapTags(Habit h) =>
        h.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color)).ToList();
}
