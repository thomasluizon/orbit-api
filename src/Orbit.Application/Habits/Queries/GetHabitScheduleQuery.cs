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
    bool IsLoggedInRange,
    IReadOnlyList<HabitInstanceItem> Instances,
    IReadOnlyList<SearchMatchField>? SearchMatches = null,
    string? Emoji = null);

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
    IReadOnlyList<SearchMatchField>? SearchMatches = null,
    string? Emoji = null);

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
    int WeekStartDay,
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
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);

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

        topLevel = HabitScheduleFilters.ApplyCommonFilters(topLevel, request, lookup);

        var filtered = topLevel.ToList();

        var totalCount = filtered.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);
        var page = Math.Max(1, Math.Min(request.Page, Math.Max(1, totalPages)));

        var ctx = new ScheduleMapContext(
            lookup,
            weekStartDay,
            IncludeAllChildren: true,
            IncludeOverdue: request.IncludeOverdue,
            Search: request.Search);
        var pagedItems = filtered
            .Skip((page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(h => HabitScheduleFilters.MapToScheduleItem(h, [], false, ctx))
            .ToList();

        return Result.Success(new PaginatedResponse<HabitScheduleItem>(
            pagedItems, page, request.PageSize, totalCount, totalPages));
    }

    private async Task<Result<PaginatedResponse<HabitScheduleItem>>> HandleScheduledHabits(
        GetHabitScheduleQuery request, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        await HabitScheduleService.AdvanceStaleBadHabitDueDates(habitRepository, unitOfWork, request.UserId, today, cancellationToken);

        var overdueLookbackDays = request.IncludeOverdue ? 31 : AppConstants.DefaultOverdueWindowDays;
        var logFrom = (request.DateFrom ?? today).AddDays(-overdueLookbackDays);
        var logTo = request.DateTo ?? today;
        var allHabits = await LoadScheduleHabits(
            request.UserId,
            logFrom,
            logTo,
            includeTags: HabitScheduleFilters.NeedsTagsForFiltering(request),
            includeGoals: false,
            cancellationToken);

        var lookup = allHabits.ToLookup(h => h.ParentHabitId);

        IEnumerable<Habit> topLevel = lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc);

        topLevel = HabitScheduleFilters.ApplyCommonFilters(topLevel, request, lookup);
        topLevel = HabitScheduleFilters.ApplyFrequencyUnitFilter(topLevel, request.FrequencyUnitFilter);

        if (!request.DateFrom.HasValue || !request.DateTo.HasValue)
            return await BuildNonDateResponse(topLevel, request, lookup, today, weekStartDay, logFrom, logTo, cancellationToken);

        var dateFrom = request.DateFrom.Value;
        var dateTo = request.DateTo.Value;

        var filtered = HabitScheduleFilters.FilterScheduledHabits(topLevel, dateFrom, dateTo, request.IncludeOverdue, lookup, weekStartDay);

        var totalCount = filtered.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);
        var page = Math.Max(1, Math.Min(request.Page, Math.Max(1, totalPages)));

        var scheduledDatesCache = new Dictionary<Guid, List<DateOnly>>();
        foreach (var item in filtered)
            scheduledDatesCache[item.habit.Id] = item.scheduledDates;

        var pageItems = filtered
            .Skip((page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToList();
        var pagedLookup = await LoadPageHabitLookup(
            pageItems.Select(x => x.habit),
            lookup,
            logFrom,
            logTo,
            includeGoals: true,
            cancellationToken);

        var ctx = new ScheduleMapContext(
            pagedLookup,
            weekStartDay,
            IncludeOverdue: request.IncludeOverdue,
            DateFrom: dateFrom,
            DateTo: dateTo,
            ReferenceDate: dateFrom,
            UserToday: today,
            Search: request.Search,
            ScheduledDatesCache: scheduledDatesCache);
        var pagedHabitsById = pagedLookup.SelectMany(group => group).ToDictionary(h => h.Id);
        var pagedItems = pageItems
            .Select(x => HabitScheduleFilters.MapToScheduleItem(
                pagedHabitsById.TryGetValue(x.habit.Id, out var hydratedHabit) ? hydratedHabit : x.habit,
                x.scheduledDates,
                x.isOverdue,
                ctx))
            .ToList();

        if (request.IncludeGeneral)
            await AppendGeneralHabits(pagedItems, request, logFrom, logTo, today, weekStartDay, cancellationToken);

        return Result.Success(new PaginatedResponse<HabitScheduleItem>(
            pagedItems,
            page,
            request.PageSize,
            totalCount,
            totalPages));
    }

    private async Task<IReadOnlyList<Habit>> LoadScheduleHabits(
        Guid userId,
        DateOnly logFrom,
        DateOnly logTo,
        bool includeTags,
        bool includeGoals,
        CancellationToken cancellationToken)
    {
        return await habitRepository.FindAsync(
            h => h.UserId == userId && !h.IsGeneral,
            q => HabitScheduleFilters.IncludeHabitGraph(q, logFrom, logTo, includeTags, includeGoals),
            cancellationToken);
    }

    private async Task<ILookup<Guid?, Habit>> LoadPageHabitLookup(
        IEnumerable<Habit> pageTopLevelHabits,
        ILookup<Guid?, Habit> baseLookup,
        DateOnly logFrom,
        DateOnly logTo,
        bool includeGoals,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        foreach (var habit in pageTopLevelHabits)
            HabitScheduleFilters.AddSubtreeIds(habit.Id, baseLookup, ids);

        if (ids.Count == 0)
            return Enumerable.Empty<Habit>().ToLookup(h => h.ParentHabitId);

        var pageHabits = await habitRepository.FindAsync(
            h => ids.Contains(h.Id),
            q => HabitScheduleFilters.IncludeHabitGraph(q, logFrom, logTo, includeTags: true, includeGoals: includeGoals),
            cancellationToken);

        return pageHabits
            .Where(h => ids.Contains(h.Id))
            .ToLookup(h => h.ParentHabitId);
    }

    private async Task<Result<PaginatedResponse<HabitScheduleItem>>> BuildNonDateResponse(
        IEnumerable<Habit> topLevel,
        GetHabitScheduleQuery request,
        ILookup<Guid?, Habit> lookup,
        DateOnly today,
        int weekStartDay,
        DateOnly logFrom,
        DateOnly logTo,
        CancellationToken cancellationToken)
    {
        var allFiltered = topLevel.ToList();
        var allTotalCount = allFiltered.Count;
        var allTotalPages = (int)Math.Ceiling((double)allTotalCount / request.PageSize);
        var allPage = Math.Max(1, Math.Min(request.Page, Math.Max(1, allTotalPages)));

        var pageHabits = allFiltered
            .Skip((allPage - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToList();
        var pagedLookup = await LoadPageHabitLookup(
            pageHabits,
            lookup,
            logFrom,
            logTo,
            includeGoals: true,
            cancellationToken);
        var pagedHabitsById = pagedLookup.SelectMany(group => group).ToDictionary(h => h.Id);

        var ctx = new ScheduleMapContext(
            pagedLookup,
            weekStartDay,
            IncludeAllChildren: true,
            IncludeOverdue: request.IncludeOverdue,
            ReferenceDate: today,
            UserToday: today,
            Search: request.Search);
        var allPagedItems = pageHabits
            .Select(h => HabitScheduleFilters.MapToScheduleItem(
                pagedHabitsById.TryGetValue(h.Id, out var hydratedHabit) ? hydratedHabit : h,
                [],
                false,
                ctx))
            .ToList();

        return Result.Success(new PaginatedResponse<HabitScheduleItem>(
            allPagedItems, allPage, request.PageSize, allTotalCount, allTotalPages));
    }

    private async Task AppendGeneralHabits(
        List<HabitScheduleItem> pagedItems,
        GetHabitScheduleQuery request,
        DateOnly logFrom,
        DateOnly logTo,
        DateOnly today,
        int weekStartDay,
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
            weekStartDay,
            IncludeAllChildren: true,
            IncludeOverdue: request.IncludeOverdue,
            UserToday: today,
            Search: request.Search);
        var generalItems = generalTopLevel
            .Select(h => HabitScheduleFilters.MapToScheduleItem(h, [], false, ctx))
            .ToList();

        pagedItems.AddRange(generalItems);
    }
}
