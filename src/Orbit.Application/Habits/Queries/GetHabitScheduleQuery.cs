using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

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
    bool IsGeneral,
    bool IsFlexible,
    IReadOnlyList<DayOfWeek> Days,
    int? Position,
    DateTime CreatedAtUtc,
    DateOnly DueDate,
    TimeOnly? DueTime,
    TimeOnly? DueEndTime,
    DateOnly? EndDate,
    IReadOnlyList<DateOnly> ScheduledDates,
    bool IsOverdue,
    bool ReminderEnabled,
    IReadOnlyList<int> ReminderTimes,
    bool SlipAlertEnabled,
    IReadOnlyList<ChecklistItem> ChecklistItems,
    IReadOnlyList<HabitTagItem> Tags,
    IReadOnlyList<HabitScheduleChildItem> Children,
    bool HasSubHabits,
    int? FlexibleTarget,
    int? FlexibleCompleted);

public record HabitScheduleChildItem(
    Guid Id,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit,
    bool IsCompleted,
    bool IsGeneral,
    bool IsFlexible,
    IReadOnlyList<DayOfWeek> Days,
    DateOnly DueDate,
    TimeOnly? DueTime,
    TimeOnly? DueEndTime,
    DateOnly? EndDate,
    int? Position,
    IReadOnlyList<ChecklistItem> ChecklistItems,
    IReadOnlyList<HabitTagItem> Tags,
    IReadOnlyList<HabitScheduleChildItem> Children,
    bool HasSubHabits,
    int? FlexibleTarget,
    int? FlexibleCompleted);

public record GetHabitScheduleQuery(
    Guid UserId,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    bool IncludeOverdue = false,
    string? Search = null,
    string? FrequencyUnitFilter = null,
    bool? IsCompleted = null,
    IReadOnlyList<Guid>? TagIds = null,
    bool? IsGeneral = null,
    int Page = 1,
    int PageSize = 50) : IRequest<Result<PaginatedResponse<HabitScheduleItem>>>;

public class GetHabitScheduleQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork) : IRequestHandler<GetHabitScheduleQuery, Result<PaginatedResponse<HabitScheduleItem>>>
{
    public async Task<Result<PaginatedResponse<HabitScheduleItem>>> Handle(GetHabitScheduleQuery request, CancellationToken cancellationToken)
    {
        if (request.IsGeneral == true)
            return await HandleGeneralHabits(request, cancellationToken);

        return await HandleScheduledHabits(request, cancellationToken);
    }

    private async Task<Result<PaginatedResponse<HabitScheduleItem>>> HandleGeneralHabits(
        GetHabitScheduleQuery request, CancellationToken cancellationToken)
    {
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsGeneral,
            q => q.Include(h => h.Tags),
            cancellationToken);

        var lookup = allHabits.ToLookup(h => h.ParentHabitId);

        IEnumerable<Habit> topLevel = lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc);

        topLevel = ApplyCommonFilters(topLevel, request, lookup);

        var filtered = topLevel.ToList();

        var totalCount = filtered.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);
        var page = Math.Max(1, Math.Min(request.Page, Math.Max(1, totalPages)));

        var pagedItems = filtered
            .Skip((page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(h => MapToScheduleItem(h, [], false, lookup, includeAllChildren: true))
            .ToList();

        return Result.Success(new PaginatedResponse<HabitScheduleItem>(
            pagedItems, page, request.PageSize, totalCount, totalPages));
    }

    private async Task<Result<PaginatedResponse<HabitScheduleItem>>> HandleScheduledHabits(
        GetHabitScheduleQuery request, CancellationToken cancellationToken)
    {
        // Advance stale bad habit DueDates so they show on the correct next scheduled day
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var staleBadHabits = await habitRepository.FindTrackedAsync(
            h => h.UserId == request.UserId && h.IsBadHabit && h.FrequencyUnit != null && h.DueDate < today
                && (!h.EndDate.HasValue || h.EndDate.Value >= today),
            cancellationToken);

        if (staleBadHabits.Count > 0)
        {
            foreach (var habit in staleBadHabits)
                habit.AdvanceDueDate(today);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Include Logs for flexible habits so we can compute window progress
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && !h.IsGeneral,
            q => q.Include(h => h.Tags).Include(h => h.Logs),
            cancellationToken);

        var lookup = allHabits.ToLookup(h => h.ParentHabitId);

        IEnumerable<Habit> topLevel = lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc);

        topLevel = ApplyCommonFilters(topLevel, request, lookup);

        // Frequency unit filter (only relevant for scheduled habits)
        if (!string.IsNullOrWhiteSpace(request.FrequencyUnitFilter))
        {
            if (request.FrequencyUnitFilter.Equals("none", StringComparison.OrdinalIgnoreCase))
                topLevel = topLevel.Where(h => h.FrequencyUnit == null);
            else if (Enum.TryParse<FrequencyUnit>(request.FrequencyUnitFilter, true, out var unit))
                topLevel = topLevel.Where(h => h.FrequencyUnit == unit);
        }

        // No date range: return all habits without schedule computation (used by "all" view)
        if (!request.DateFrom.HasValue || !request.DateTo.HasValue)
        {
            var allFiltered = topLevel.ToList();
            var allTotalCount = allFiltered.Count;
            var allTotalPages = (int)Math.Ceiling((double)allTotalCount / request.PageSize);
            var allPage = Math.Max(1, Math.Min(request.Page, Math.Max(1, allTotalPages)));

            var allPagedItems = allFiltered
                .Skip((allPage - 1) * request.PageSize)
                .Take(request.PageSize)
                .Select(h => MapToScheduleItem(h, [], false, lookup, includeAllChildren: true, referenceDate: today))
                .ToList();

            return Result.Success(new PaginatedResponse<HabitScheduleItem>(
                allPagedItems, allPage, request.PageSize, allTotalCount, allTotalPages));
        }

        var dateFrom = request.DateFrom.Value;
        var dateTo = request.DateTo.Value;

        // Schedule-aware filtering: keep habits that have at least one scheduled date in range,
        // OR are overdue, OR have any descendant due in range
        var filtered = new List<(Habit habit, List<DateOnly> scheduledDates, bool isOverdue)>();

        foreach (var habit in topLevel)
        {
            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo);
            var isOverdue = false;

            // Flexible habits should NOT appear as overdue
            if (!habit.IsFlexible && request.IncludeOverdue && !habit.IsCompleted && !habit.IsBadHabit && habit.DueDate < dateFrom
                && (!habit.EndDate.HasValue || habit.EndDate.Value >= dateFrom))
            {
                isOverdue = true;
            }

            var hasDescendantDue = HasAnyDescendantDue(habit.Id, lookup, dateFrom, dateTo);

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
            .Select(x => MapToScheduleItem(x.habit, [], x.isOverdue, lookup, dateFrom: dateFrom, dateTo: dateTo, referenceDate: dateFrom))
            .ToList();

        return Result.Success(new PaginatedResponse<HabitScheduleItem>(
            pagedItems,
            page,
            request.PageSize,
            totalCount,
            totalPages));
    }

    private static IEnumerable<Habit> ApplyCommonFilters(
        IEnumerable<Habit> topLevel,
        GetHabitScheduleQuery request,
        ILookup<Guid?, Habit> lookup)
    {
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

        return topLevel;
    }

    private static HabitScheduleItem MapToScheduleItem(
        Habit h,
        List<DateOnly> scheduledDates,
        bool isOverdue,
        ILookup<Guid?, Habit> lookup,
        bool includeAllChildren = false,
        DateOnly? dateFrom = null,
        DateOnly? dateTo = null,
        DateOnly? referenceDate = null)
    {
        int? flexibleTarget = null;
        int? flexibleCompleted = null;
        if (h.IsFlexible && referenceDate.HasValue)
        {
            flexibleTarget = h.FrequencyQuantity ?? 1;
            flexibleCompleted = HabitScheduleService.GetCompletedInWindow(h, referenceDate.Value, h.Logs);
        }

        return new HabitScheduleItem(
            h.Id, h.Title, h.Description, h.FrequencyUnit, h.FrequencyQuantity,
            h.IsBadHabit, h.IsCompleted, h.IsGeneral, h.IsFlexible,
            h.Days.ToList(), h.Position, h.CreatedAtUtc,
            h.DueDate, h.DueTime, h.DueEndTime, h.EndDate,
            scheduledDates, isOverdue,
            h.ReminderEnabled, h.ReminderTimes, h.SlipAlertEnabled,
            h.ChecklistItems, MapTags(h),
            MapChildren(h.Id, lookup, includeAllChildren, dateFrom, dateTo, referenceDate),
            lookup[h.Id].Any(),
            flexibleTarget, flexibleCompleted);
    }

    private static bool HasAnyDescendantDue(Guid parentId, ILookup<Guid?, Habit> lookup, DateOnly dateFrom, DateOnly dateTo)
    {
        foreach (var child in lookup[parentId])
        {
            if (HabitScheduleService.GetScheduledDates(child, dateFrom, dateTo).Count > 0)
                return true;
            if (!child.IsCompleted && !child.IsBadHabit && child.DueDate < dateFrom)
                return true;
            if (HasAnyDescendantDue(child.Id, lookup, dateFrom, dateTo))
                return true;
        }
        return false;
    }

    private static List<HabitScheduleChildItem> MapChildren(
        Guid parentId, ILookup<Guid?, Habit> lookup,
        bool includeAll = false, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        DateOnly? referenceDate = null)
    {
        var children = lookup[parentId];

        if (!includeAll && dateFrom.HasValue && dateTo.HasValue)
        {
            var df = dateFrom.Value;
            var dt = dateTo.Value;
            children = children
                .Where(c => HabitScheduleService.GetScheduledDates(c, df, dt).Count > 0
                    || c.IsCompleted
                    || (!c.IsCompleted && !c.IsBadHabit && c.DueDate < df)
                    || HasAnyDescendantDue(c.Id, lookup, df, dt));
        }

        return children
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c =>
            {
                int? ft = null;
                int? fc = null;
                if (c.IsFlexible && referenceDate.HasValue)
                {
                    ft = c.FrequencyQuantity ?? 1;
                    fc = HabitScheduleService.GetCompletedInWindow(c, referenceDate.Value, c.Logs);
                }
                return new HabitScheduleChildItem(
                    c.Id, c.Title, c.Description,
                    c.FrequencyUnit, c.FrequencyQuantity, c.IsBadHabit, c.IsCompleted, c.IsGeneral, c.IsFlexible,
                    c.Days.ToList(), c.DueDate, c.DueTime, c.DueEndTime, c.EndDate,
                    c.Position, c.ChecklistItems, MapTags(c),
                    MapChildren(c.Id, lookup, includeAll, dateFrom, dateTo, referenceDate),
                    lookup[c.Id].Any(), ft, fc);
            })
            .ToList();
    }

    private static List<HabitTagItem> MapTags(Habit h) =>
        h.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color)).ToList();
}
