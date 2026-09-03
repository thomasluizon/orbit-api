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
            return Result.Failure<HabitWidgetResponse>(ErrorMessages.UserNotFound);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        await HabitScheduleService.AdvanceStaleBadHabitDueDates(
            habitRepository,
            unitOfWork,
            request.UserId,
            today,
            cancellationToken,
            user.WeekStartDay);

        var habits = await LoadWidgetHabits(request.UserId, today, cancellationToken);
        var lookup = habits.ToLookup(h => h.ParentHabitId);
        var todayItems = BuildItems(lookup, today, user.WeekStartDay);

        var selectedOffset = 0;
        var selectedItems = todayItems;
        if (ShouldShowTomorrow(todayItems))
        {
            var tomorrow = today.AddDays(1);
            var tomorrowItems = BuildItems(lookup, tomorrow, user.WeekStartDay);
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

    private static bool ShouldShowTomorrow(List<HabitWidgetItem> todayItems)
    {
        return todayItems.Count == 0 || todayItems.All(item => item.IsCompleted);
    }

    private static List<HabitWidgetItem> BuildItems(
        ILookup<Guid?, Habit> lookup,
        DateOnly date,
        int weekStartDay)
    {
        return lookup[null]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc)
            .Where(h => IsVisibleOnWidget(h, lookup, date, weekStartDay))
            .Select(h => MapItem(h, lookup, date, weekStartDay))
            .ToList();
    }

    private static bool IsVisibleOnWidget(
        Habit habit,
        ILookup<Guid?, Habit> lookup,
        DateOnly date,
        int weekStartDay)
    {
        var scheduledDates = HabitScheduleService.GetScheduledDates(habit, date, date, weekStartDay);
        return scheduledDates.Count > 0
            || (!habit.IsCompleted && HabitScheduleService.IsOverdueOnDate(habit, date, weekStartDay))
            || IsLoggedOnDate(habit, date)
            || HasVisibleDescendant(habit.Id, lookup, date, weekStartDay);
    }

    private static bool HasVisibleDescendant(
        Guid parentId,
        ILookup<Guid?, Habit> lookup,
        DateOnly date,
        int weekStartDay)
    {
        return lookup[parentId].Any(child => IsVisibleOnWidget(child, lookup, date, weekStartDay));
    }

    private static HabitWidgetItem MapItem(
        Habit habit,
        ILookup<Guid?, Habit> lookup,
        DateOnly date,
        int weekStartDay)
    {
        var children = lookup[habit.Id]
            .OrderBy(h => h.Position ?? int.MaxValue)
            .ThenBy(h => h.CreatedAtUtc)
            .Where(h => IsVisibleOnWidget(h, lookup, date, weekStartDay))
            .Select(h => MapItem(h, lookup, date, weekStartDay))
            .ToList();
        var isCompleted = habit.IsCompleted || IsLoggedOnDate(habit, date) || (children.Count > 0 && children.All(c => c.IsCompleted));

        return new HabitWidgetItem(
            habit.Id,
            habit.Title,
            isCompleted,
            !isCompleted && HabitScheduleService.IsOverdueOnDate(habit, date, weekStartDay),
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
}
