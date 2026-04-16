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

public record CalendarMonthResponse(
    IReadOnlyList<HabitScheduleItem> Habits,
    Dictionary<Guid, List<HabitLogResponse>> Logs);

public record GetCalendarMonthQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo) : IRequest<Result<CalendarMonthResponse>>;

/// <summary>
/// Groups the parameters needed for mapping habits in the calendar context.
/// </summary>
internal record CalendarMapContext(
    ILookup<Guid?, Habit> ChildLookup,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    DateOnly? ReferenceDate = null,
    DateOnly? UserToday = null);

public class GetCalendarMonthQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork) : IRequestHandler<GetCalendarMonthQuery, Result<CalendarMonthResponse>>
{
    public async Task<Result<CalendarMonthResponse>> Handle(GetCalendarMonthQuery request, CancellationToken cancellationToken)
    {
        // Validate date range (max 62 days to cover month + adjacent partial weeks)
        if (request.DateTo.DayNumber - request.DateFrom.DayNumber > AppConstants.MaxCalendarRangeDays)
            return Result.Failure<CalendarMonthResponse>("Date range must not exceed 62 days.");

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        // Advance stale bad habit DueDates (same as HandleScheduledHabits)
        await HabitScheduleService.AdvanceStaleBadHabitDueDates(habitRepository, unitOfWork, request.UserId, today, cancellationToken);

        var allHabits = await LoadHabitsWithLogs(request, cancellationToken);
        var lookup = allHabits.ToLookup(h => h.ParentHabitId);
        var topLevel = lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc)
            .ToList();

        var ctx = new CalendarMapContext(lookup, request.DateFrom, request.DateTo, request.DateFrom, today);
        var habitItems = BuildScheduleItems(topLevel, request.DateFrom, request.DateTo, lookup, ctx);
        var logsDict = BuildLogsDict(allHabits, request.DateFrom, request.DateTo);

        return Result.Success(new CalendarMonthResponse(habitItems, logsDict));
    }

    private async Task<IReadOnlyList<Habit>> LoadHabitsWithLogs(
        GetCalendarMonthQuery request, CancellationToken cancellationToken)
    {
        var logFrom = request.DateFrom.AddDays(-31); // lookback for overdue detection
        return await habitRepository.FindAsync(
            h => h.UserId == request.UserId && !h.IsGeneral,
            q => q.Include(h => h.Tags)
                  .Include(h => h.Logs.Where(l => l.Date >= logFrom && l.Date <= request.DateTo))
                  .Include(h => h.Goals),
            cancellationToken);
    }

    private static List<HabitScheduleItem> BuildScheduleItems(
        List<Habit> topLevel,
        DateOnly dateFrom,
        DateOnly dateTo,
        ILookup<Guid?, Habit> lookup,
        CalendarMapContext ctx)
    {
        var habitItems = new List<HabitScheduleItem>();
        foreach (var habit in topLevel)
        {
            if (habit.IsFlexible && !HabitScheduleService.IsFlexibleHabitDueOnDate(habit, dateFrom, habit.Logs))
                continue;

            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo);
            var isOverdue = DetermineOverdueStatus(habit, dateFrom, scheduledDates);
            var hasDescendantDue = HasAnyDescendantDue(habit.Id, lookup, dateFrom, dateTo);

            if (scheduledDates.Count > 0 || isOverdue || hasDescendantDue)
                habitItems.Add(MapToScheduleItem(habit, scheduledDates, isOverdue, ctx));
        }
        return habitItems;
    }

    /// <summary>
    /// Determines whether a habit is overdue based on its type and schedule.
    /// </summary>
    private static bool DetermineOverdueStatus(
        Habit habit, DateOnly dateFrom, List<DateOnly> scheduledDates)
    {
        if (habit.IsFlexible || habit.IsBadHabit)
            return false;

        // One-time tasks overdue
        if (habit.FrequencyUnit == null)
        {
            return !habit.IsCompleted
                && habit.DueDate < dateFrom
                && (!habit.EndDate.HasValue || habit.EndDate.Value >= dateFrom);
        }

        // Recurring overdue check
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
        if (habit.DueDate > lookbackStart) lookbackStart = habit.DueDate;
        var pastDates = HabitScheduleService.GetScheduledDates(habit, lookbackStart, dateFrom.AddDays(-1));
        var logDates = habit.Logs.Select(l => l.Date).ToHashSet();
        return pastDates.Any(d => !logDates.Contains(d));
    }

    private static Dictionary<Guid, List<HabitLogResponse>> BuildLogsDict(
        IReadOnlyList<Habit> allHabits, DateOnly dateFrom, DateOnly dateTo)
    {
        return allHabits
            .Where(h => h.ParentHabitId == null)
            .ToDictionary(
                h => h.Id,
                h => h.Logs
                    .Where(l => l.Date >= dateFrom && l.Date <= dateTo)
                    .OrderByDescending(l => l.Date)
                    .Select(l => new HabitLogResponse(l.Id, l.Date, l.Value, l.CreatedAtUtc))
                    .ToList());
    }

    private static HabitScheduleItem MapToScheduleItem(
        Habit habit,
        List<DateOnly> scheduledDates,
        bool isOverdue,
        CalendarMapContext ctx)
    {
        var children = ctx.ChildLookup[habit.Id]
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChildItem(c, ctx))
            .ToList();

        var instances = (ctx.DateFrom.HasValue && ctx.DateTo.HasValue && ctx.UserToday.HasValue)
            ? HabitScheduleService.GetInstances(habit, ctx.DateFrom.Value, ctx.DateTo.Value, ctx.UserToday.Value)
            : [];

        var (flexibleTarget, flexibleCompleted) = CalculateFlexibleProgress(habit, ctx.ReferenceDate);

        return new HabitScheduleItem(
            habit.Id, habit.Title, habit.Description,
            habit.FrequencyUnit, habit.FrequencyQuantity,
            habit.IsBadHabit, habit.IsCompleted, habit.IsGeneral, habit.IsFlexible,
            habit.Days.ToList(), habit.Position, habit.CreatedAtUtc,
            habit.DueDate, habit.DueTime, habit.DueEndTime, habit.EndDate,
            scheduledDates, isOverdue,
            habit.ReminderEnabled, habit.ReminderTimes, habit.ScheduledReminders,
            habit.SlipAlertEnabled, habit.ChecklistItems,
            habit.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color)).ToList(),
            habit.Goals.Select(g => new LinkedGoalDto(g.Id, g.Title)).ToList(),
            children, children.Count > 0,
            flexibleTarget, flexibleCompleted, instances);
    }

    private static (int? Target, int? Completed) CalculateFlexibleProgress(
        Habit habit, DateOnly? referenceDate)
    {
        if (!habit.IsFlexible || !referenceDate.HasValue)
            return (null, null);

        var totalTarget = habit.FrequencyQuantity ?? 1;
        var skipped = HabitScheduleService.GetSkippedInWindow(habit, referenceDate.Value, habit.Logs);
        var target = Math.Max(0, totalTarget - skipped);
        var completed = HabitScheduleService.GetCompletedInWindow(habit, referenceDate.Value, habit.Logs);
        return (target, completed);
    }

    private static HabitScheduleChildItem MapChildItem(Habit child, CalendarMapContext ctx)
    {
        var grandchildren = ctx.ChildLookup[child.Id]
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChildItem(c, ctx))
            .ToList();

        var isLoggedInRange = ctx.DateFrom.HasValue && ctx.DateTo.HasValue &&
            child.Logs.Any(l => l.Date >= ctx.DateFrom.Value && l.Date <= ctx.DateTo.Value && l.Value > 0);

        int? flexTarget = null;
        int? flexCompleted = null;
        if (child.IsFlexible && ctx.DateFrom.HasValue)
        {
            flexTarget = child.FrequencyQuantity;
            flexCompleted = HabitScheduleService.GetCompletedInWindow(child, ctx.DateFrom.Value, child.Logs);
        }

        var instances = (ctx.DateFrom.HasValue && ctx.DateTo.HasValue && ctx.UserToday.HasValue)
            ? HabitScheduleService.GetInstances(child, ctx.DateFrom.Value, ctx.DateTo.Value, ctx.UserToday.Value)
            : [];

        return new HabitScheduleChildItem(
            child.Id, child.Title, child.Description,
            child.FrequencyUnit, child.FrequencyQuantity,
            child.IsBadHabit, child.IsCompleted, child.IsGeneral, child.IsFlexible,
            child.Days.ToList(), child.DueDate, child.DueTime, child.DueEndTime, child.EndDate,
            child.Position, child.ChecklistItems,
            child.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color)).ToList(),
            grandchildren, grandchildren.Count > 0,
            flexTarget, flexCompleted, isLoggedInRange, instances);
    }

    private static bool HasAnyDescendantDue(Guid parentId, ILookup<Guid?, Habit> lookup, DateOnly dateFrom, DateOnly dateTo)
    {
        foreach (var child in lookup[parentId])
        {
            var childDates = HabitScheduleService.GetScheduledDates(child, dateFrom, dateTo);
            if (childDates.Count > 0) return true;
            if (HasAnyDescendantDue(child.Id, lookup, dateFrom, dateTo)) return true;
        }
        return false;
    }
}
