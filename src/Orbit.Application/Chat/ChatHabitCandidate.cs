using Orbit.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat;

public sealed record ChatHabitCandidate(
    HabitScheduleSnapshot Schedule,
    string Title,
    int? Position,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ChecklistItem>? ChecklistItems,
    bool CompletedToday,
    string? Emoji)
{
    public const string QueryTag = "astra-habit-candidates";

    public static IQueryable<ChatHabitCandidate> Select(IQueryable<Habit> query, DateOnly today, bool includeChecklists) =>
        query.TagWith(QueryTag).Select(habit => new ChatHabitCandidate(
            new HabitScheduleSnapshot(habit.Id, habit.ParentHabitId, habit.FrequencyUnit, habit.FrequencyQuantity,
                habit.IntervalWeeks, habit.NextDueDate, habit.ScheduledStartDate, habit.OriginalDayOfMonth,
                habit.EndDate, habit.CreatedAtUtc, habit.DeletedAtUtc, habit.IsDeleted, habit.IsBadHabit,
                habit.IsCompleted, habit.Days.ToList(), habit.Schedule, habit.StartDate, habit.MonthDays),
            habit.Title, habit.Position, habit.UpdatedAtUtc,
            includeChecklists && !habit.IsCompleted ? habit.ChecklistItems : null,
            habit.Logs.Any(log => !log.IsDeleted && log.Date == today && log.Value > 0 && !log.IsSlip), habit.Emoji));

    internal Habit ToHabit(Guid userId)
    {
        var habit = Habit.FromScheduleSnapshot(Schedule, userId);
        habit.LoadScheduleCandidateFieldsForRead(Title, null, Position, [], Emoji);
        habit.UpdateChecklist(ChecklistItems ?? []);
        habit.UpdatedAtUtc = UpdatedAtUtc;
        return habit;
    }
}

public sealed record ChatHabitTags(Guid HabitId, IReadOnlyList<Tag> Tags)
{
    public static IQueryable<ChatHabitTags> Select(IQueryable<Habit> query) => query
        .Where(habit => habit.Tags.Any())
        .Select(habit => new ChatHabitTags(habit.Id, habit.Tags.ToList()));
}
