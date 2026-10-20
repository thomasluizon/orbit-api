using Orbit.Application.Common;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

internal static class HabitUpdateValidation
{
    public static UpdateHabitCommand ToCommand(Guid userId, Guid habitId, HabitUpdateParams update) =>
        new(userId, habitId, update.Title, update.Description,
            update.FrequencyUnit, update.FrequencyQuantity, update.IsBadHabit, update.DueDate,
            IsGeneral: update.IsGeneral, ClearEndDate: update.ClearEndDate, Emoji: update.Emoji,
            IntervalWeeks: update.IntervalWeeks,
            Options: new UpdateHabitCommandOptions(update.Days, update.DueTime, update.DueEndTime,
                update.ReminderEnabled, update.ReminderTimes, update.SlipAlertEnabled, update.ChecklistItems,
                update.ScheduledReminders, update.EndDate, update.IsFlexible, update.RelativeReminders));

    public static async Task<Result> ValidateAsync(Guid userId, Habit habit, HabitUpdateParams update,
        IGenericRepository<Habit> repository, CancellationToken ct, IReadOnlyList<Habit>? related = null)
    {
        var command = ToCommand(userId, habit.Id, update);
        var validation = await new UpdateHabitCommandValidator(repository).ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result.Failure(validation.ToString());
        return update.IsGeneral is { } isGeneral
            ? ValidateGeneral(habit, isGeneral, related ?? await LoadRelatedAsync([habit], userId, repository, ct))
            : Result.Success();
    }

    public static Task<IReadOnlyList<Habit>> LoadRelatedAsync(IReadOnlyList<Habit> habits, Guid userId,
        IGenericRepository<Habit> repository, CancellationToken ct)
    {
        var ids = habits.Select(h => h.Id).ToArray();
        var parentIds = habits.Where(h => h.ParentHabitId.HasValue).Select(h => h.ParentHabitId!.Value).ToArray();
        return repository.FindAsync(h => h.UserId == userId
            && ((h.ParentHabitId.HasValue && ids.Contains(h.ParentHabitId.Value)) || parentIds.Contains(h.Id)), ct);
    }

    public static Result ValidateGeneral(Habit habit, bool isGeneral, IReadOnlyList<Habit> related)
    {
        if (habit.ParentHabitId is not null)
        {
            var parent = related.FirstOrDefault(h => h.Id == habit.ParentHabitId);
            return parent is not null && parent.IsGeneral != isGeneral
                ? Result.Failure(ErrorMessages.GeneralMismatchWithParent)
                : Result.Success();
        }
        return related.Any(c => c.ParentHabitId == habit.Id && c.IsGeneral != isGeneral
                && !(c.IsCompleted && c.FrequencyUnit is null))
            ? Result.Failure(ErrorMessages.GeneralMismatchWithChildren)
            : Result.Success();
    }
}
