using Orbit.Application.Common;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

internal static class HabitUpdateValidation
{
    public static async Task<Result> ValidateAsync(Guid userId, Habit habit, HabitUpdateParams update,
        IGenericRepository<Habit> repository, CancellationToken ct)
    {
        var command = new UpdateHabitCommand(userId, habit.Id, update.Title, update.Description,
            update.FrequencyUnit, update.FrequencyQuantity, update.IsBadHabit, update.DueDate,
            IsGeneral: update.IsGeneral, ClearEndDate: update.ClearEndDate, Emoji: update.Emoji,
            IntervalWeeks: update.IntervalWeeks,
            Options: new UpdateHabitCommandOptions(update.Days, update.DueTime, update.DueEndTime,
                update.ReminderEnabled, update.ReminderTimes, update.SlipAlertEnabled, update.ChecklistItems,
                update.ScheduledReminders, update.EndDate, update.IsFlexible, update.RelativeReminders));
        var validation = await new UpdateHabitCommandValidator(repository).ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result.Failure(validation.ToString());
        return update.IsGeneral is { } isGeneral
            ? await ValidateGeneralAsync(habit, isGeneral, userId, repository, ct)
            : Result.Success();
    }

    public static async Task<Result> ValidateGeneralAsync(Habit habit, bool isGeneral, Guid userId,
        IGenericRepository<Habit> repository, CancellationToken ct)
    {
        if (habit.ParentHabitId is not null)
        {
            var parent = await repository.FindOneTrackedAsync(
                h => h.Id == habit.ParentHabitId && h.UserId == userId, cancellationToken: ct);
            return parent is not null && parent.IsGeneral != isGeneral
                ? Result.Failure(ErrorMessages.GeneralMismatchWithParent)
                : Result.Success();
        }
        var children = await repository.FindAsync(h => h.ParentHabitId == habit.Id && h.UserId == userId, ct);
        return children.Any(c => c.IsGeneral != isGeneral && !(c.IsCompleted && c.FrequencyUnit is null))
            ? Result.Failure(ErrorMessages.GeneralMismatchWithChildren)
            : Result.Success();
    }
}
