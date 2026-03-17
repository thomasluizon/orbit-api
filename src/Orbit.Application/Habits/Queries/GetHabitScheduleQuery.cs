using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

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
    IReadOnlyList<DateOnly> ScheduledDates,
    bool IsOverdue,
    IReadOnlyList<HabitScheduleChildItem> Children);

public record HabitScheduleChildItem(
    Guid Id,
    string Title,
    string? Description,
    bool IsCompleted,
    int? Position,
    IReadOnlyList<HabitScheduleChildItem> Children);

public record GetHabitScheduleQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    bool IncludeOverdue = false,
    string? Search = null,
    string? FrequencyUnitFilter = null,
    bool? IsCompleted = null,
    int Page = 1,
    int PageSize = 50) : IRequest<PaginatedResponse<HabitScheduleItem>>;

public class GetHabitScheduleQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitScheduleQuery, PaginatedResponse<HabitScheduleItem>>
{
    public async Task<PaginatedResponse<HabitScheduleItem>> Handle(GetHabitScheduleQuery request, CancellationToken cancellationToken)
    {
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsActive,
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

        // Schedule-aware filtering: keep habits that have at least one scheduled date in range
        // OR are overdue (if includeOverdue is true and we're checking today)
        var filtered = new List<(Habit habit, List<DateOnly> scheduledDates, bool isOverdue)>();

        foreach (var habit in topLevel)
        {
            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, request.DateFrom, request.DateTo);
            var isOverdue = false;

            if (request.IncludeOverdue && !habit.IsCompleted && habit.DueDate < request.DateFrom)
            {
                isOverdue = true;
            }

            if (scheduledDates.Count > 0 || isOverdue)
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
            scheduledDates,
            isOverdue,
            MapChildren(h.Id, lookup, dateFrom, dateTo));

    private static List<HabitScheduleChildItem> MapChildren(Guid parentId, ILookup<Guid?, Habit> lookup, DateOnly dateFrom, DateOnly dateTo) =>
        lookup[parentId]
            .Where(c => HabitScheduleService.GetScheduledDates(c, dateFrom, dateTo).Count > 0 || c.IsCompleted)
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => new HabitScheduleChildItem(
                c.Id, c.Title, c.Description, c.IsCompleted,
                c.Position, MapChildren(c.Id, lookup, dateFrom, dateTo)))
            .ToList();
}
