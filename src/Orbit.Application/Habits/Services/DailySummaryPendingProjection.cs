using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

public static class DailySummaryPendingProjection
{
    public static IReadOnlyList<DailySummaryPendingHabit> Project(
        IReadOnlyList<HabitScheduleItem> schedule, DateOnly today)
    {
        var pending = new List<DailySummaryPendingHabit>();
        var dayOrder = 0;
        foreach (var item in schedule)
        {
            Append(item.Id, item.Title, item.ParentHabitId, item.ParentTitle, item.DueDate,
                item.DueTime, item.DueEndTime, item.Position, item.IsBadHabit, item.IsCompleted,
                item.IsLoggedInRange, item.IsOverdue, item.FlexibleTarget, item.FlexibleCompleted,
                item.Instances, item.Children);
        }

        return pending.OrderBy(item => item.DueDate)
            .ThenBy(item => item.DueTime ?? item.DueEndTime ?? TimeOnly.MaxValue)
            .ThenBy(item => item.DayOrder).ToList();

        bool Append(Guid id, string title, Guid? parentId, string? parentTitle, DateOnly dueDate,
            TimeOnly? dueTime, TimeOnly? dueEndTime, int? position, bool badHabit, bool completed,
            bool logged, bool overdue, int? target, int? count,
            IReadOnlyList<HabitInstanceItem> instances, IReadOnlyList<HabitScheduleChildItem> children)
        {
            var order = dayOrder++;
            var hasPendingChildren = false;
            foreach (var child in children)
            {
                hasPendingChildren |= Append(child.Id, child.Title, id, title, child.DueDate,
                    child.DueTime, child.DueEndTime, child.Position, child.IsBadHabit, child.IsCompleted,
                    child.IsLoggedInRange, child.IsOverdue, child.FlexibleTarget, child.FlexibleCompleted,
                    child.Instances, child.Children);
            }

            var occurrence = instances.Where(instance => instance.Date <= today
                    && (instance.Date == today || overdue)
                    && instance.Status is InstanceStatus.Pending or InstanceStatus.Overdue)
                .OrderBy(instance => instance.Date).FirstOrDefault();
            var isPending = !badHabit && !completed && (target.HasValue
                ? count.GetValueOrDefault() < target.Value
                : occurrence is not null || overdue && !logged);
            if (!isPending && !hasPendingChildren)
                return false;

            var total = children.Count > 0 ? children.Count : target ?? 1;
            var done = children.Count > 0
                ? children.Count(child => child.IsLoggedInRange && !child.IsBadHabit)
                : count ?? (logged ? 1 : 0);
            pending.Add(new(id, title, parentId, parentTitle, occurrence?.Date ?? (overdue ? dueDate : today),
                dueTime, dueEndTime, position, order, overdue, hasPendingChildren, done, total));
            return true;
        }
    }
}
