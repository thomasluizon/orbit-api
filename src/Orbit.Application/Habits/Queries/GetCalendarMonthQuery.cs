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

        // Load ALL habits (no completion filter, no pagination) with logs for the range
        var logFrom = request.DateFrom.AddDays(-31); // lookback for overdue detection
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && !h.IsGeneral,
            q => q.Include(h => h.Tags)
                  .Include(h => h.Logs.Where(l => l.Date >= logFrom && l.Date <= request.DateTo))
                  .Include(h => h.Goals),
            cancellationToken);

        var lookup = allHabits.ToLookup(h => h.ParentHabitId);
        var topLevel = lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc)
            .ToList();

        // Build schedule items for each habit that has activity in the range
        var habitItems = new List<HabitScheduleItem>();
        foreach (var habit in topLevel)
        {
            // Flexible habits that have met their window target
            if (habit.IsFlexible && !HabitScheduleService.IsFlexibleHabitDueOnDate(habit, request.DateFrom, habit.Logs))
                continue;

            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, request.DateFrom, request.DateTo);
            var isOverdue = false;

            // One-time tasks overdue
            if (!habit.IsFlexible && habit.FrequencyUnit == null && !habit.IsCompleted && !habit.IsBadHabit
                && habit.DueDate < request.DateFrom && (!habit.EndDate.HasValue || habit.EndDate.Value >= request.DateFrom))
            {
                isOverdue = true;
            }

            // Recurring overdue check
            if (!isOverdue && !habit.IsFlexible && habit.FrequencyUnit != null && !habit.IsBadHabit
                && !scheduledDates.Contains(request.DateFrom))
            {
                var qty = habit.FrequencyQuantity ?? 1;
                var lookbackDays = HabitScheduleService.GetLookbackDays(habit.FrequencyUnit, qty);
                var lookbackStart = request.DateFrom.AddDays(-lookbackDays);
                if (habit.DueDate > lookbackStart) lookbackStart = habit.DueDate;
                var pastDates = HabitScheduleService.GetScheduledDates(habit, lookbackStart, request.DateFrom.AddDays(-1));
                var logDates = habit.Logs.Select(l => l.Date).ToHashSet();
                if (pastDates.Any(d => !logDates.Contains(d)))
                    isOverdue = true;
            }

            var hasDescendantDue = HasAnyDescendantDue(habit.Id, lookup, request.DateFrom, request.DateTo);

            if (scheduledDates.Count > 0 || isOverdue || hasDescendantDue)
            {
                habitItems.Add(MapToScheduleItem(habit, scheduledDates, isOverdue, lookup,
                    request.DateFrom, request.DateTo, request.DateFrom, today));
            }
        }

        // Build logs dictionary from already-loaded habit logs (no second query needed)
        var logsDict = allHabits
            .Where(h => h.ParentHabitId == null)
            .ToDictionary(
                h => h.Id,
                h => h.Logs
                    .Where(l => l.Date >= request.DateFrom && l.Date <= request.DateTo)
                    .OrderByDescending(l => l.Date)
                    .Select(l => new HabitLogResponse(l.Id, l.Date, l.Value, l.Note, l.CreatedAtUtc))
                    .ToList());

        return Result.Success(new CalendarMonthResponse(habitItems, logsDict));
    }

    // Simplified MapToScheduleItem for calendar context (reuses existing record types)
    private static HabitScheduleItem MapToScheduleItem(
        Habit habit,
        List<DateOnly> scheduledDates,
        bool isOverdue,
        ILookup<Guid?, Habit> lookup,
        DateOnly? dateFrom = null,
        DateOnly? dateTo = null,
        DateOnly? referenceDate = null,
        DateOnly? userToday = null)
    {
        var children = lookup[habit.Id]
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChildItem(c, dateFrom, dateTo, lookup, userToday))
            .ToList();

        var instances = (dateFrom.HasValue && dateTo.HasValue && userToday.HasValue)
            ? HabitScheduleService.GetInstances(habit, dateFrom.Value, dateTo.Value, userToday.Value)
            : [];

        int? flexibleTarget = null;
        int? flexibleCompleted = null;
        if (habit.IsFlexible && referenceDate.HasValue)
        {
            var totalTarget = habit.FrequencyQuantity ?? 1;
            var skipped = HabitScheduleService.GetSkippedInWindow(habit, referenceDate.Value, habit.Logs);
            flexibleTarget = Math.Max(0, totalTarget - skipped);
            flexibleCompleted = HabitScheduleService.GetCompletedInWindow(habit, referenceDate.Value, habit.Logs);
        }

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

    private static HabitScheduleChildItem MapChildItem(
        Habit child,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        ILookup<Guid?, Habit> lookup,
        DateOnly? userToday)
    {
        var grandchildren = lookup[child.Id]
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChildItem(c, dateFrom, dateTo, lookup, userToday))
            .ToList();

        var isLoggedInRange = dateFrom.HasValue && dateTo.HasValue &&
            child.Logs.Any(l => l.Date >= dateFrom.Value && l.Date <= dateTo.Value && l.Value > 0);

        int? flexTarget = null;
        int? flexCompleted = null;
        if (child.IsFlexible && dateFrom.HasValue)
        {
            flexTarget = child.FrequencyQuantity;
            flexCompleted = HabitScheduleService.GetCompletedInWindow(child, dateFrom.Value, child.Logs);
        }

        var instances = (dateFrom.HasValue && dateTo.HasValue && userToday.HasValue)
            ? HabitScheduleService.GetInstances(child, dateFrom.Value, dateTo.Value, userToday.Value)
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
