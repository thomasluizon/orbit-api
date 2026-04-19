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
    IReadOnlyList<DateOnly> ScheduledDates,
    bool IsOverdue,
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
    int PageSize = 50,
    bool IncludeGeneral = false) : IRequest<Result<PaginatedResponse<HabitScheduleItem>>>;

/// <summary>
/// Groups the parameters needed for mapping habits to schedule items,
/// reducing parameter count on MapToScheduleItem and MapChildren (S107).
/// </summary>
internal record ScheduleMapContext(
    ILookup<Guid?, Habit> ChildLookup,
    bool IncludeAllChildren = false,
    bool IncludeOverdue = false,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    DateOnly? ReferenceDate = null,
    DateOnly? UserToday = null,
    string? Search = null,
    Dictionary<Guid, List<DateOnly>>? ScheduledDatesCache = null)
{
    /// <summary>
    /// Returns cached scheduled dates for a habit, computing and caching on first access.
    /// Falls back to direct computation when no cache or no date range is available.
    /// </summary>
    public List<DateOnly> GetScheduledDates(Habit habit)
    {
        if (ScheduledDatesCache is null || !DateFrom.HasValue || !DateTo.HasValue)
            return HabitScheduleService.GetScheduledDates(habit, DateFrom ?? default, DateTo ?? default);

        if (!ScheduledDatesCache.TryGetValue(habit.Id, out var dates))
        {
            dates = HabitScheduleService.GetScheduledDates(habit, DateFrom.Value, DateTo.Value);
            ScheduledDatesCache[habit.Id] = dates;
        }
        return dates;
    }
}

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
            q => q.Include(h => h.Tags)
                  .Include(h => h.Logs)
                  .Include(h => h.Goals),
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

        var ctx = new ScheduleMapContext(
            lookup,
            IncludeAllChildren: true,
            IncludeOverdue: request.IncludeOverdue,
            Search: request.Search);
        var pagedItems = filtered
            .Skip((page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(h => MapToScheduleItem(h, [], false, ctx))
            .ToList();

        return Result.Success(new PaginatedResponse<HabitScheduleItem>(
            pagedItems, page, request.PageSize, totalCount, totalPages));
    }

    private async Task<Result<PaginatedResponse<HabitScheduleItem>>> HandleScheduledHabits(
        GetHabitScheduleQuery request, CancellationToken cancellationToken)
    {
        // Advance stale bad habit DueDates so they show on the correct next scheduled day
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        await HabitScheduleService.AdvanceStaleBadHabitDueDates(habitRepository, unitOfWork, request.UserId, today, cancellationToken);

        // Include Logs for flexible habits so we can compute window progress
        // Filter logs to the requested date range (extended by overdue window) to avoid loading all historical logs
        // Load logs with enough lookback for recurring overdue detection (monthly habits need ~31 days)
        var overdueLookbackDays = request.IncludeOverdue ? 31 : AppConstants.DefaultOverdueWindowDays;
        var logFrom = (request.DateFrom ?? today).AddDays(-overdueLookbackDays);
        var logTo = request.DateTo ?? today;
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && !h.IsGeneral,
            q => q.Include(h => h.Tags)
                  .Include(h => h.Logs.Where(l => l.Date >= logFrom && l.Date <= logTo))
                  .Include(h => h.Goals),
            cancellationToken);

        var lookup = allHabits.ToLookup(h => h.ParentHabitId);

        IEnumerable<Habit> topLevel = lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc);

        topLevel = ApplyCommonFilters(topLevel, request, lookup);
        topLevel = ApplyFrequencyUnitFilter(topLevel, request.FrequencyUnitFilter);

        // No date range: return all habits without schedule computation (used by "all" view)
        if (!request.DateFrom.HasValue || !request.DateTo.HasValue)
            return BuildNonDateResponse(topLevel, request, lookup, today);

        var dateFrom = request.DateFrom.Value;
        var dateTo = request.DateTo.Value;

        // Schedule-aware filtering: keep habits that have at least one scheduled date in range,
        // OR are overdue, OR have any descendant due in range
        var filtered = FilterScheduledHabits(topLevel, dateFrom, dateTo, request.IncludeOverdue, lookup);

        // Pagination
        var totalCount = filtered.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);
        var page = Math.Max(1, Math.Min(request.Page, Math.Max(1, totalPages)));

        var scheduledDatesCache = new Dictionary<Guid, List<DateOnly>>();
        // Seed the cache with dates already computed during filtering
        foreach (var item in filtered)
            scheduledDatesCache[item.habit.Id] = item.scheduledDates;

        var ctx = new ScheduleMapContext(
            lookup,
            IncludeOverdue: request.IncludeOverdue,
            DateFrom: dateFrom,
            DateTo: dateTo,
            ReferenceDate: dateFrom,
            UserToday: today,
            Search: request.Search,
            ScheduledDatesCache: scheduledDatesCache);
        var pagedItems = filtered
            .Skip((page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => MapToScheduleItem(x.habit, x.scheduledDates, x.isOverdue, ctx))
            .ToList();

        // When IncludeGeneral is true, append general habits after the scheduled ones
        if (request.IncludeGeneral)
            await AppendGeneralHabits(pagedItems, request, logFrom, logTo, today, cancellationToken);

        return Result.Success(new PaginatedResponse<HabitScheduleItem>(
            pagedItems,
            page,
            request.PageSize,
            totalCount,
            totalPages));
    }

    private static IEnumerable<Habit> ApplyFrequencyUnitFilter(
        IEnumerable<Habit> habits, string? frequencyUnitFilter)
    {
        if (string.IsNullOrWhiteSpace(frequencyUnitFilter))
            return habits;

        if (frequencyUnitFilter.Equals("none", StringComparison.OrdinalIgnoreCase))
            return habits.Where(h => h.FrequencyUnit == null);

        if (Enum.TryParse<FrequencyUnit>(frequencyUnitFilter, true, out var unit))
            return habits.Where(h => h.FrequencyUnit == unit);

        return habits;
    }

    private static Result<PaginatedResponse<HabitScheduleItem>> BuildNonDateResponse(
        IEnumerable<Habit> topLevel,
        GetHabitScheduleQuery request,
        ILookup<Guid?, Habit> lookup,
        DateOnly today)
    {
        var allFiltered = topLevel.ToList();
        var allTotalCount = allFiltered.Count;
        var allTotalPages = (int)Math.Ceiling((double)allTotalCount / request.PageSize);
        var allPage = Math.Max(1, Math.Min(request.Page, Math.Max(1, allTotalPages)));

        var ctx = new ScheduleMapContext(
            lookup,
            IncludeAllChildren: true,
            IncludeOverdue: request.IncludeOverdue,
            ReferenceDate: today,
            UserToday: today,
            Search: request.Search);
        var allPagedItems = allFiltered
            .Skip((allPage - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(h => MapToScheduleItem(h, [], false, ctx))
            .ToList();

        return Result.Success(new PaginatedResponse<HabitScheduleItem>(
            allPagedItems, allPage, request.PageSize, allTotalCount, allTotalPages));
    }

    private static List<(Habit habit, List<DateOnly> scheduledDates, bool isOverdue)> FilterScheduledHabits(
        IEnumerable<Habit> topLevel,
        DateOnly dateFrom,
        DateOnly dateTo,
        bool includeOverdue,
        ILookup<Guid?, Habit> lookup)
    {
        var filtered = new List<(Habit habit, List<DateOnly> scheduledDates, bool isOverdue)>();

        foreach (var habit in topLevel)
        {
            // Flexible habits that have met their window target should not appear
            if (habit.IsFlexible && !HabitScheduleService.IsFlexibleHabitDueOnDate(habit, dateFrom, habit.Logs))
                continue;

            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo);
            var isOverdue = DetermineOverdueStatus(habit, dateFrom, scheduledDates, includeOverdue);
            var hasDescendantDue = HasAnyDescendantDue(
                habit.Id,
                lookup,
                dateFrom,
                dateTo,
                includeOverdue);

            if (scheduledDates.Count > 0 || isOverdue || hasDescendantDue)
                filtered.Add((habit, scheduledDates, isOverdue));
        }

        return filtered;
    }

    /// <summary>
    /// Determines whether a habit is overdue based on its type and schedule.
    /// Handles both one-time tasks and recurring habits.
    /// </summary>
    private static bool DetermineOverdueStatus(
        Habit habit, DateOnly dateFrom, List<DateOnly> scheduledDates, bool includeOverdue)
    {
        if (!includeOverdue || habit.IsFlexible || habit.IsBadHabit)
            return false;

        // One-time tasks: overdue if due date has passed
        if (habit.FrequencyUnit == null)
        {
            return !habit.IsCompleted
                && habit.DueDate < dateFrom
                && (!habit.EndDate.HasValue || habit.EndDate.Value >= dateFrom);
        }

        // Recurring habits: overdue if a past occurrence was missed and habit is not due today
        if (scheduledDates.Contains(dateFrom))
            return false;

        return HasMissedRecentOccurrence(habit, dateFrom);
    }

    /// <summary>
    /// Checks whether a recurring habit has any missed occurrence in its lookback window.
    /// </summary>
    private static bool HasMissedRecentOccurrence(Habit habit, DateOnly dateFrom)
    {
        var qty = habit.FrequencyQuantity ?? 1;
        var lookbackDays = HabitScheduleService.GetLookbackDays(habit.FrequencyUnit, qty);

        var lookbackStart = dateFrom.AddDays(-lookbackDays);
        if (habit.DueDate > lookbackStart)
            lookbackStart = habit.DueDate;

        var pastDates = HabitScheduleService.GetScheduledDates(habit, lookbackStart, dateFrom.AddDays(-1));
        var logDates = habit.Logs.Select(l => l.Date).ToHashSet();

        return pastDates.Any(d => !logDates.Contains(d));
    }

    private async Task AppendGeneralHabits(
        List<HabitScheduleItem> pagedItems,
        GetHabitScheduleQuery request,
        DateOnly logFrom,
        DateOnly logTo,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var generalHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsGeneral,
            q => q.Include(h => h.Tags)
                  .Include(h => h.Logs.Where(l => l.Date >= logFrom && l.Date <= logTo))
                  .Include(h => h.Goals),
            cancellationToken);

        var generalLookup = generalHabits.ToLookup(h => h.ParentHabitId);
        var generalTopLevel = generalLookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc)
            .ToList();

        var ctx = new ScheduleMapContext(
            generalLookup,
            IncludeAllChildren: true,
            IncludeOverdue: request.IncludeOverdue,
            UserToday: today,
            Search: request.Search);
        var generalItems = generalTopLevel
            .Select(h => MapToScheduleItem(h, [], false, ctx))
            .ToList();

        pagedItems.AddRange(generalItems);
    }

    private static IEnumerable<Habit> ApplyCommonFilters(
        IEnumerable<Habit> topLevel,
        GetHabitScheduleQuery request,
        ILookup<Guid?, Habit> lookup)
    {
        if (!string.IsNullOrWhiteSpace(request.Search))
            topLevel = ApplySearchFilter(
                topLevel,
                request.Search.Trim(),
                request.DateFrom,
                request.DateTo,
                request.IncludeOverdue,
                lookup);

        if (request.IsCompleted.HasValue)
            topLevel = topLevel.Where(h => h.IsCompleted == request.IsCompleted.Value);

        if (request.TagIds is { Count: > 0 })
            topLevel = ApplyTagFilter(topLevel, request.TagIds, lookup);

        return topLevel;
    }

    private static IEnumerable<Habit> ApplySearchFilter(
        IEnumerable<Habit> topLevel,
        string term,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        bool includeOverdue,
        ILookup<Guid?, Habit> lookup)
    {
        return topLevel.Where(h => MatchesSearch(h, term, lookup, dateFrom, dateTo, includeOverdue));
    }

    private static bool MatchesSearch(
        Habit h,
        string term,
        ILookup<Guid?, Habit> lookup,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        bool includeOverdue)
    {
        if (FuzzyMatcher.FuzzyContains(h.Title, term)) return true;
        if (h.Description != null && FuzzyMatcher.FuzzyContains(h.Description, term)) return true;
        if (h.Tags.Any(t => FuzzyMatcher.FuzzyContains(t.Name, term))) return true;
        return HasDescendantMatchingSearch(h.Id, lookup, term, dateFrom, dateTo, includeOverdue);
    }

    private static bool HasDescendantMatchingSearch(
        Guid parentId,
        ILookup<Guid?, Habit> lookup,
        string term,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        bool includeOverdue)
    {
        foreach (var child in lookup[parentId])
        {
            if (!IsChildRelevantForSearch(child, dateFrom, dateTo, includeOverdue)) continue;
            if (FuzzyMatcher.FuzzyContains(child.Title, term)) return true;
            if (HasDescendantMatchingSearch(child.Id, lookup, term, dateFrom, dateTo, includeOverdue))
                return true;
        }
        return false;
    }

    private static bool IsChildRelevantForSearch(
        Habit child,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        bool includeOverdue)
    {
        if (child.IsCompleted) return false;
        if (!dateFrom.HasValue || !dateTo.HasValue) return true;

        var scheduledDates = HabitScheduleService.GetScheduledDates(child, dateFrom.Value, dateTo.Value);
        var isOverdue = DetermineOverdueStatus(child, dateFrom.Value, scheduledDates, includeOverdue);

        return scheduledDates.Count > 0 || isOverdue;
    }

    private static IEnumerable<Habit> ApplyTagFilter(
        IEnumerable<Habit> topLevel,
        IReadOnlyList<Guid> tagIds,
        ILookup<Guid?, Habit> lookup)
    {
        var tagIdSet = tagIds.ToHashSet();
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
        return topLevel.Where(h => HasMatchingTag(h) || HasDescendantWithTag(h.Id));
    }

    private static HabitScheduleItem MapToScheduleItem(
        Habit h,
        List<DateOnly> scheduledDates,
        bool isOverdue,
        ScheduleMapContext ctx)
    {
        var (flexibleTarget, flexibleCompleted) = CalculateFlexibleProgress(h, ctx.ReferenceDate);

        var instances = ctx.DateFrom.HasValue && ctx.DateTo.HasValue && ctx.UserToday.HasValue
            ? HabitScheduleService.GetInstances(h, ctx.DateFrom.Value, ctx.DateTo.Value, ctx.UserToday.Value)
            : [];

        return new HabitScheduleItem(
            h.Id, h.Title, h.Description, h.FrequencyUnit, h.FrequencyQuantity,
            h.IsBadHabit, h.IsCompleted, h.IsGeneral, h.IsFlexible,
            h.Days.ToList(), h.Position, h.CreatedAtUtc,
            h.DueDate, h.DueTime, h.DueEndTime, h.EndDate,
            scheduledDates, isOverdue,
            h.ReminderEnabled, h.ReminderTimes, h.ScheduledReminders, h.SlipAlertEnabled,
            h.ChecklistItems, MapTags(h), MapGoals(h),
            MapChildren(h.Id, ctx),
            ctx.ChildLookup[h.Id].Any(),
            flexibleTarget, flexibleCompleted,
            instances,
            ComputeSearchMatches(h, ctx));
    }

    private static (int? Target, int? Completed) CalculateFlexibleProgress(
        Habit h, DateOnly? referenceDate)
    {
        if (!h.IsFlexible || !referenceDate.HasValue)
            return (null, null);

        var totalTarget = h.FrequencyQuantity ?? 1;
        var skipped = HabitScheduleService.GetSkippedInWindow(h, referenceDate.Value, h.Logs);
        var target = Math.Max(0, totalTarget - skipped);
        var completed = HabitScheduleService.GetCompletedInWindow(h, referenceDate.Value, h.Logs);
        return (target, completed);
    }

    private static List<SearchMatchField>? ComputeSearchMatches(Habit h, ScheduleMapContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Search)) return null;

        var matches = new List<SearchMatchField>();
        if (FuzzyMatcher.FuzzyContains(h.Title, ctx.Search))
            matches.Add(new SearchMatchField("title", null));
        if (h.Description != null && FuzzyMatcher.FuzzyContains(h.Description, ctx.Search))
            matches.Add(new SearchMatchField("description", null));
        matches.AddRange(h.Tags
            .Where(tag => FuzzyMatcher.FuzzyContains(tag.Name, ctx.Search))
            .Select(tag => new SearchMatchField("tag", tag.Name)));
        AddChildSearchMatches(matches, h.Id, ctx);
        return matches.Count > 0 ? matches : null;
    }

    private static void AddChildSearchMatches(
        List<SearchMatchField> matches, Guid parentId, ScheduleMapContext ctx)
    {
        foreach (var child in ctx.ChildLookup[parentId])
        {
            if (child.IsCompleted) continue;
            if (ctx.DateFrom.HasValue && ctx.DateTo.HasValue)
            {
                var childScheduledDates = ctx.GetScheduledDates(child);
                var childIsOverdue = DetermineOverdueStatus(
                    child,
                    ctx.DateFrom.Value,
                    childScheduledDates,
                    ctx.IncludeOverdue);

                if (childScheduledDates.Count == 0 && !childIsOverdue)
                    continue;
            }

            if (FuzzyMatcher.FuzzyContains(child.Title, ctx.Search!))
                matches.Add(new SearchMatchField("child", child.Title));
        }
    }

    private static bool HasAnyDescendantDue(
        Guid parentId,
        ILookup<Guid?, Habit> lookup,
        DateOnly dateFrom,
        DateOnly dateTo,
        bool includeOverdue)
    {
        foreach (var child in lookup[parentId])
        {
            var scheduledDates = HabitScheduleService.GetScheduledDates(child, dateFrom, dateTo);
            var isOverdue = DetermineOverdueStatus(child, dateFrom, scheduledDates, includeOverdue);

            if (scheduledDates.Count > 0 || isOverdue)
                return true;
            if (child.Logs.Any(l => l.Date >= dateFrom && l.Date <= dateTo))
                return true;
            if (HasAnyDescendantDue(child.Id, lookup, dateFrom, dateTo, includeOverdue))
                return true;
        }
        return false;
    }

    private static List<HabitScheduleChildItem> MapChildren(Guid parentId, ScheduleMapContext ctx)
    {
        var children = ctx.ChildLookup[parentId];

        if (!ctx.IncludeAllChildren && ctx.DateFrom.HasValue && ctx.DateTo.HasValue)
        {
            var df = ctx.DateFrom.Value;
            var dt = ctx.DateTo.Value;
            children = children
                .Where(c =>
                {
                    var scheduledDates = ctx.GetScheduledDates(c);
                    var isOverdue = DetermineOverdueStatus(c, df, scheduledDates, ctx.IncludeOverdue);

                    return scheduledDates.Count > 0
                        || c.IsCompleted
                        || isOverdue
                        || HasAnyDescendantDue(c.Id, ctx.ChildLookup, df, dt, ctx.IncludeOverdue)
                        || c.Logs.Any(l => l.Date >= df && l.Date <= dt);
                });
        }

        return children
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapSingleChild(c, ctx))
            .ToList();
    }

    private static HabitScheduleChildItem MapSingleChild(Habit c, ScheduleMapContext ctx)
    {
        var (ft, fc) = CalculateFlexibleProgress(c, ctx.ReferenceDate);
        var isLoggedInRange = ctx.DateFrom.HasValue && ctx.DateTo.HasValue
            && c.Logs.Any(l => l.Date >= ctx.DateFrom.Value && l.Date <= ctx.DateTo.Value && l.Value > 0);
        var scheduledDates = ctx.DateFrom.HasValue && ctx.DateTo.HasValue
            ? ctx.GetScheduledDates(c)
            : [];
        var isOverdue = ctx.DateFrom.HasValue
            && DetermineOverdueStatus(c, ctx.DateFrom.Value, scheduledDates, ctx.IncludeOverdue);

        var instances = ctx.DateFrom.HasValue && ctx.DateTo.HasValue && ctx.UserToday.HasValue
            ? HabitScheduleService.GetInstances(c, ctx.DateFrom.Value, ctx.DateTo.Value, ctx.UserToday.Value)
            : [];

        return new HabitScheduleChildItem(
            c.Id, c.Title, c.Description,
            c.FrequencyUnit, c.FrequencyQuantity, c.IsBadHabit, c.IsCompleted, c.IsGeneral, c.IsFlexible,
            c.Days.ToList(), c.DueDate, c.DueTime, c.DueEndTime, c.EndDate,
            scheduledDates, isOverdue,
            c.Position, c.ChecklistItems, MapTags(c),
            MapChildren(c.Id, ctx),
            ctx.ChildLookup[c.Id].Any(), ft, fc, isLoggedInRange,
            instances,
            ComputeSearchMatches(c, ctx));
    }

    private static List<HabitTagItem> MapTags(Habit h) =>
        h.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color)).ToList();

    private static List<LinkedGoalDto> MapGoals(Habit h) =>
        h.Goals.Select(g => new LinkedGoalDto(g.Id, g.Title)).ToList();
}
