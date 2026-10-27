using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat;

public sealed record ChatHabitCandidate(
    HabitScheduleSnapshot Schedule,
    string Title,
    int? Position,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ChecklistItem>? ChecklistItems,
    bool CompletedToday)
{
    public static IQueryable<ChatHabitCandidate> Select(IQueryable<Habit> query, DateOnly today, bool includeChecklists) =>
        query.Select(habit => new ChatHabitCandidate(
            new HabitScheduleSnapshot(habit.Id, habit.ParentHabitId, habit.FrequencyUnit, habit.FrequencyQuantity,
                habit.IntervalWeeks, habit.NextDueDate, habit.ScheduledStartDate, habit.OriginalDayOfMonth,
                habit.EndDate, habit.CreatedAtUtc, habit.DeletedAtUtc, habit.IsDeleted, habit.IsBadHabit,
                habit.IsCompleted, habit.Days.ToList(), habit.Schedule, habit.StartDate),
            habit.Title, habit.Position, habit.UpdatedAtUtc,
            includeChecklists && !habit.IsCompleted ? habit.ChecklistItems : null,
            habit.Logs.Any(log => !log.IsDeleted && log.Date == today && log.Value > 0 && !log.IsSlip)));

    internal Habit ToHabit(Guid userId)
    {
        var habit = Habit.FromScheduleSnapshot(Schedule, userId);
        habit.LoadScheduleCandidateFieldsForRead(Title, null, Position, []);
        habit.UpdateChecklist(ChecklistItems ?? []);
        habit.UpdatedAtUtc = UpdatedAtUtc;
        return habit;
    }
}
