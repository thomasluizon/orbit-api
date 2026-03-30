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
public record LinkedGoalDto(Guid Id, string Title);
public record SearchMatchField(string Field, string? Value);

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
    IReadOnlyList<ScheduledReminderTime> ScheduledReminders,
    bool SlipAlertEnabled,
    IReadOnlyList<ChecklistItem> ChecklistItems,
    IReadOnlyList<HabitTagItem> Tags,
    IReadOnlyList<LinkedGoalDto> LinkedGoals,
    IReadOnlyList<HabitScheduleChildItem> Children,
    bool HasSubHabits,
    int? FlexibleTarget,
    int? FlexibleCompleted,
    IReadOnlyList<HabitInstanceItem> Instances,
    IReadOnlyList<SearchMatchField>? SearchMatches = null);

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
    int? FlexibleCompleted,
    bool IsLoggedInRange,
    IReadOnlyList<HabitInstanceItem> Instances,
    IReadOnlyList<SearchMatchField>? SearchMatches = null);

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
            q => q.Include(h => h.Tags).Include(h => h.Logs).Include(h => h.Goals),
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
            .Select(h => MapToScheduleItem(h, [], false, lookup, includeAllChildren: true, userToday: null, search: request.Search))
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
            q => q.Include(h => h.Tags).Include(h => h.Logs).Include(h => h.Goals),
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
                .Select(h => MapToScheduleItem(h, [], false, lookup, includeAllChildren: true, referenceDate: today, userToday: today, search: request.Search))
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
            // Flexible habits that have met their window target should not appear
            if (habit.IsFlexible && !HabitScheduleService.IsFlexibleHabitDueOnDate(habit, dateFrom, habit.Logs))
                continue;

            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo);
            var isOverdue = false;

            // Flexible habits should NOT appear as overdue
            if (!habit.IsFlexible && habit.FrequencyUnit == null && request.IncludeOverdue && !habit.IsCompleted && !habit.IsBadHabit && habit.DueDate < dateFrom
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
            .Select(x => MapToScheduleItem(x.habit, [], x.isOverdue, lookup, dateFrom: dateFrom, dateTo: dateTo, referenceDate: dateFrom, userToday: today, search: request.Search))
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
            var df = request.DateFrom;
            var dt = request.DateTo;
            bool IsChildRelevant(Habit child)
            {
                if (child.IsCompleted) return false;
                if (!df.HasValue || !dt.HasValue) return true;
                return HabitScheduleService.GetScheduledDates(child, df.Value, dt.Value).Count > 0
                    || (!child.IsBadHabit && !child.IsFlexible && child.FrequencyUnit == null && child.DueDate >= df.Value && child.DueDate <= dt.Value);
            }
            bool MatchesSearch(Habit h)
            {
                if (FuzzyMatcher.FuzzyContains(h.Title, term)) return true;
                if (h.Description != null && FuzzyMatcher.FuzzyContains(h.Description, term)) return true;
                if (h.Tags.Any(t => FuzzyMatcher.FuzzyContains(t.Name, term))) return true;
                return HasDescendantMatchingSearch(h.Id, lookup, term);
            }
            bool HasDescendantMatchingSearch(Guid parentId, ILookup<Guid?, Habit> lkp, string t)
            {
                foreach (var child in lkp[parentId])
                {
                    if (!IsChildRelevant(child)) continue;
                    if (FuzzyMatcher.FuzzyContains(child.Title, t)) return true;
                    if (HasDescendantMatchingSearch(child.Id, lkp, t)) return true;
                }
                return false;
            }
            topLevel = topLevel.Where(MatchesSearch);
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
        DateOnly? referenceDate = null,
        DateOnly? userToday = null,
        string? search = null)
    {
        int? flexibleTarget = null;
        int? flexibleCompleted = null;
        if (h.IsFlexible && referenceDate.HasValue)
        {
            var totalTarget = h.FrequencyQuantity ?? 1;
            var skipped = HabitScheduleService.GetSkippedInWindow(h, referenceDate.Value, h.Logs);
            flexibleTarget = Math.Max(0, totalTarget - skipped);
            flexibleCompleted = HabitScheduleService.GetCompletedInWindow(h, referenceDate.Value, h.Logs);
        }

        var instances = dateFrom.HasValue && dateTo.HasValue && userToday.HasValue
            ? HabitScheduleService.GetInstances(h, dateFrom.Value, dateTo.Value, userToday.Value)
            : [];

        return new HabitScheduleItem(
            h.Id, h.Title, h.Description, h.FrequencyUnit, h.FrequencyQuantity,
            h.IsBadHabit, h.IsCompleted, h.IsGeneral, h.IsFlexible,
            h.Days.ToList(), h.Position, h.CreatedAtUtc,
            h.DueDate, h.DueTime, h.DueEndTime, h.EndDate,
            scheduledDates, isOverdue,
            h.ReminderEnabled, h.ReminderTimes, h.ScheduledReminders, h.SlipAlertEnabled,
            h.ChecklistItems, MapTags(h), MapGoals(h),
            MapChildren(h.Id, lookup, includeAllChildren, dateFrom, dateTo, referenceDate, userToday, search),
            lookup[h.Id].Any(),
            flexibleTarget, flexibleCompleted,
            instances,
            ComputeSearchMatches(h, search, lookup, dateFrom, dateTo));
    }

    private static List<SearchMatchField>? ComputeSearchMatches(
        Habit h, string? search, ILookup<Guid?, Habit> lookup,
        DateOnly? dateFrom = null, DateOnly? dateTo = null)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var matches = new List<SearchMatchField>();
        if (FuzzyMatcher.FuzzyContains(h.Title, search))
            matches.Add(new SearchMatchField("title", null));
        if (h.Description != null && FuzzyMatcher.FuzzyContains(h.Description, search))
            matches.Add(new SearchMatchField("description", null));
        foreach (var tag in h.Tags)
        {
            if (FuzzyMatcher.FuzzyContains(tag.Name, search))
                matches.Add(new SearchMatchField("tag", tag.Name));
        }
        foreach (var child in lookup[h.Id])
        {
            if (child.IsCompleted) continue;
            if (dateFrom.HasValue && dateTo.HasValue
                && HabitScheduleService.GetScheduledDates(child, dateFrom.Value, dateTo.Value).Count == 0
                && (child.IsBadHabit || child.IsFlexible || child.FrequencyUnit != null || child.DueDate < dateFrom.Value || child.DueDate > dateTo.Value))
                continue;
            if (FuzzyMatcher.FuzzyContains(child.Title, search))
                matches.Add(new SearchMatchField("child", child.Title));
        }
        return matches.Count > 0 ? matches : null;
    }

    private static bool HasAnyDescendantDue(Guid parentId, ILookup<Guid?, Habit> lookup, DateOnly dateFrom, DateOnly dateTo)
    {
        foreach (var child in lookup[parentId])
        {
            if (HabitScheduleService.GetScheduledDates(child, dateFrom, dateTo).Count > 0)
                return true;
            if (!child.IsCompleted && !child.IsBadHabit && !child.IsFlexible && child.DueDate < dateFrom)
                return true;
            if (child.Logs.Any(l => l.Date >= dateFrom && l.Date <= dateTo))
                return true;
            if (HasAnyDescendantDue(child.Id, lookup, dateFrom, dateTo))
                return true;
        }
        return false;
    }

    private static List<HabitScheduleChildItem> MapChildren(
        Guid parentId, ILookup<Guid?, Habit> lookup,
        bool includeAll = false, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        DateOnly? referenceDate = null, DateOnly? userToday = null,
        string? search = null)
    {
        var children = lookup[parentId];

        if (!includeAll && dateFrom.HasValue && dateTo.HasValue)
        {
            var df = dateFrom.Value;
            var dt = dateTo.Value;
            children = children
                .Where(c => HabitScheduleService.GetScheduledDates(c, df, dt).Count > 0
                    || c.IsCompleted
                    || (!c.IsCompleted && !c.IsBadHabit && !c.IsFlexible && c.DueDate < df)
                    || HasAnyDescendantDue(c.Id, lookup, df, dt)
                    || c.Logs.Any(l => l.Date >= df && l.Date <= dt));
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
                    var childTotalTarget = c.FrequencyQuantity ?? 1;
                    var childSkipped = HabitScheduleService.GetSkippedInWindow(c, referenceDate.Value, c.Logs);
                    ft = Math.Max(0, childTotalTarget - childSkipped);
                    fc = HabitScheduleService.GetCompletedInWindow(c, referenceDate.Value, c.Logs);
                }
                var isLoggedInRange = dateFrom.HasValue && dateTo.HasValue
                    && c.Logs.Any(l => l.Date >= dateFrom.Value && l.Date <= dateTo.Value && l.Value > 0);

                var instances = dateFrom.HasValue && dateTo.HasValue && userToday.HasValue
                    ? HabitScheduleService.GetInstances(c, dateFrom.Value, dateTo.Value, userToday.Value)
                    : [];

                return new HabitScheduleChildItem(
                    c.Id, c.Title, c.Description,
                    c.FrequencyUnit, c.FrequencyQuantity, c.IsBadHabit, c.IsCompleted, c.IsGeneral, c.IsFlexible,
                    c.Days.ToList(), c.DueDate, c.DueTime, c.DueEndTime, c.EndDate,
                    c.Position, c.ChecklistItems, MapTags(c),
                    MapChildren(c.Id, lookup, includeAll, dateFrom, dateTo, referenceDate, userToday, search),
                    lookup[c.Id].Any(), ft, fc, isLoggedInRange,
                    instances,
                    ComputeSearchMatches(c, search, lookup, dateFrom, dateTo));
            })
            .ToList();
    }

    private static List<HabitTagItem> MapTags(Habit h) =>
        h.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color)).ToList();

    private static List<LinkedGoalDto> MapGoals(Habit h) =>
        h.Goals.Select(g => new LinkedGoalDto(g.Id, g.Title)).ToList();
}
