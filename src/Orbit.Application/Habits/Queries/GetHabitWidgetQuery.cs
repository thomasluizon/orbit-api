using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record HabitWidgetItem(
    Guid Id,
    string Title,
    bool IsCompleted,
    bool IsOverdue,
    TimeOnly? DueTime,
    int ChecklistChecked,
    int ChecklistTotal,
    bool IsBadHabit,
    IReadOnlyList<HabitWidgetItem> Children,
    bool HasSubHabits);

public record HabitWidgetResponse(
    int DayOffset,
    string Language,
    int CurrentStreak,
    IReadOnlyList<HabitWidgetItem> Items);

public record GetHabitWidgetQuery(Guid UserId) : IRequest<Result<HabitWidgetResponse>>;

public class GetHabitWidgetQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork) : IRequestHandler<GetHabitWidgetQuery, Result<HabitWidgetResponse>>
{
    public async Task<Result<HabitWidgetResponse>> Handle(GetHabitWidgetQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<HabitWidgetResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        await HabitScheduleService.AdvanceStaleBadHabitDueDates(
            habitRepository,
            unitOfWork,
            request.UserId,
            today,
            cancellationToken);

        var habits = await LoadWidgetHabits(request.UserId, today, cancellationToken);
        var lookup = habits.ToLookup(h => h.ParentHabitId);
        var todayItems = BuildItems(lookup, today);

        var selectedOffset = 0;
        var selectedItems = todayItems;
        if (ShouldShowTomorrow(todayItems))
        {
            var tomorrow = today.AddDays(1);
            var tomorrowItems = BuildItems(lookup, tomorrow);
            if (tomorrowItems.Count > 0)
            {
                selectedOffset = 1;
                selectedItems = tomorrowItems;
            }
            else
            {
                selectedItems = [];
            }
        }

        return Result.Success(new HabitWidgetResponse(
            selectedOffset,
            user.Language ?? "en",
            user.CurrentStreak,
            selectedItems));
    }

    private async Task<IReadOnlyList<Habit>> LoadWidgetHabits(
        Guid userId,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var logFrom = today.AddDays(-AppConstants.MaxRangeDays);
        var logTo = today.AddDays(1);

        return await habitRepository.FindAsync(
            h => h.UserId == userId && !h.IsGeneral,
            q => q.Include(h => h.Logs.Where(l => l.Date >= logFrom && l.Date <= logTo)),
            cancellationToken);
    }

    private static bool ShouldShowTomorrow(IReadOnlyList<HabitWidgetItem> todayItems)
    {
        return todayItems.Count == 0 || todayItems.All(item => item.IsCompleted);
    }

    private static List<HabitWidgetItem> BuildItems(ILookup<Guid?, Habit> lookup, DateOnly date)
    {
        return lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc)
            .Where(h => IsVisibleOnWidget(h, lookup, date))
            .Select(h => MapItem(h, lookup, date))
            .ToList();
    }

    private static bool IsVisibleOnWidget(Habit habit, ILookup<Guid?, Habit> lookup, DateOnly date)
    {
        var scheduledDates = HabitScheduleService.GetScheduledDates(habit, date, date);
        return scheduledDates.Count > 0
            || IsOverdue(habit, date, scheduledDates)
            || IsLoggedOnDate(habit, date)
            || HasVisibleDescendant(habit.Id, lookup, date);
    }

    private static bool HasVisibleDescendant(Guid parentId, ILookup<Guid?, Habit> lookup, DateOnly date)
    {
        foreach (var child in lookup[parentId])
        {
            if (IsVisibleOnWidget(child, lookup, date))
                return true;
        }

        return false;
    }

    private static HabitWidgetItem MapItem(Habit habit, ILookup<Guid?, Habit> lookup, DateOnly date)
    {
        var scheduledDates = HabitScheduleService.GetScheduledDates(habit, date, date);
        var children = lookup[habit.Id]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc)
            .Where(h => IsVisibleOnWidget(h, lookup, date))
            .Select(h => MapItem(h, lookup, date))
            .ToList();
        var isCompleted = habit.IsCompleted || IsLoggedOnDate(habit, date) || (children.Count > 0 && children.All(c => c.IsCompleted));

        return new HabitWidgetItem(
            habit.Id,
            habit.Title,
            isCompleted,
            !isCompleted && IsOverdue(habit, date, scheduledDates),
            habit.DueTime,
            habit.ChecklistItems.Count(item => item.IsChecked),
            habit.ChecklistItems.Count,
            habit.IsBadHabit,
            children,
            lookup[habit.Id].Any());
    }

    private static bool IsLoggedOnDate(Habit habit, DateOnly date)
    {
        return habit.Logs.Any(log => log.Date == date && log.Value > 0);
    }

    private static bool IsOverdue(Habit habit, DateOnly date, List<DateOnly> scheduledDates)
    {
        if (habit.IsFlexible || habit.IsBadHabit || habit.IsCompleted)
            return false;

        if (habit.FrequencyUnit is null)
        {
            return habit.DueDate < date
                && (!habit.EndDate.HasValue || habit.EndDate.Value >= date);
        }

        if (scheduledDates.Contains(date))
            return false;

        var quantity = habit.FrequencyQuantity ?? 1;
        var lookbackDays = Math.Min(
            HabitScheduleService.GetLookbackDays(habit.FrequencyUnit, quantity),
            AppConstants.MaxRangeDays);
        var lookbackStart = date.AddDays(-lookbackDays);
        if (habit.DueDate > lookbackStart)
            lookbackStart = habit.DueDate;

        var pastDates = HabitScheduleService.GetScheduledDates(habit, lookbackStart, date.AddDays(-1));
        var logDates = habit.Logs.Select(log => log.Date).ToHashSet();
        return pastDates.Any(pastDate => !logDates.Contains(pastDate));
    }
}
