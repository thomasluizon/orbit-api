using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
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

    public static Result ValidateGeneral(Habit habit, bool isGeneral, IReadOnlyList<Habit> related) =>
        ValidateGeneral(habit, isGeneral, related, habit.ParentHabitId);

    public static Result ValidateGeneral(Habit habit, bool isGeneral, IReadOnlyList<Habit> related, Guid? finalParentId)
    {
        if (finalParentId is not null)
        {
            var parent = related.FirstOrDefault(h => h.Id == finalParentId);
            if (parent is not null && (parent.Schedule == HabitSchedule.General) != isGeneral)
                return Result.Failure(ErrorMessages.GeneralMismatchWithParent);
        }
        return related.Any(c => c.ParentHabitId == habit.Id && (c.Schedule == HabitSchedule.General) != isGeneral
                && !(c.IsCompleted && c.Schedule is HabitSchedule.OneTime or HabitSchedule.General))
            ? Result.Failure(ErrorMessages.GeneralMismatchWithChildren)
            : Result.Success();
    }

    public static HabitUpdateParams ResolveSingleUpdate(Habit habit, BulkHabitChanges changes, DateOnly today) =>
        ResolveUpdate(habit, changes, today) with
        {
            FrequencyQuantity = changes.HasFrequencyQuantity ? changes.FrequencyQuantity ?? habit.FrequencyQuantity : habit.FrequencyQuantity,
            IntervalWeeks = changes.HasIntervalWeeks ? changes.IntervalWeeks ?? habit.IntervalWeeks : habit.IntervalWeeks,
            Days = changes.HasDays ? changes.Days : habit.Days.ToList()
        };

    public static HabitUpdateParams ResolveUpdate(Habit habit, BulkHabitChanges changes, DateOnly today)
    {
        var dueTime = changes.HasDueTime ? changes.DueTime : habit.DueTime;
        var (reminderTimes, scheduledReminders) = ReminderStoreNormalizer.NormalizeForUpdate(
            dueTime,
            habit.DueTime,
            changes.HasReminderTimes ? changes.ReminderTimes?.ToList() ?? [] : null,
            changes.HasScheduledReminders ? changes.ScheduledReminders?.ToList() ?? [] : null,
            habit.ReminderTimes,
            habit.ScheduledReminders);

        return ApplyExtendedFields(new HabitUpdateParams(
            changes.HasTitle ? changes.Title ?? habit.Title : habit.Title,
            changes.HasDescription ? changes.Description : habit.Description,
            changes.HasFrequencyUnit ? changes.FrequencyUnit : habit.FrequencyUnit,
            changes.HasFrequencyQuantity ? changes.FrequencyQuantity : habit.FrequencyQuantity,
            changes.HasIsFlexible && changes.IsFlexible ? [] : changes.HasDays ? changes.Days : habit.Days.ToList(),
            changes.HasIsBadHabit ? changes.IsBadHabit : habit.IsBadHabit,
            changes.HasDueDate ? changes.DueDate : habit.DueDate,
            DueTime: dueTime,
            DueEndTime: habit.DueEndTime,
            ReminderEnabled: changes.HasReminderEnabled ? changes.ReminderEnabled : null,
            ReminderTimes: reminderTimes,
            ChecklistItems: changes.HasChecklistItems ? changes.ChecklistItems : null,
            IsFlexible: changes.HasIsFlexible ? changes.IsFlexible : null,
            EndDate: changes.HasEndDate ? changes.EndDate : null,
            ClearEndDate: changes.HasEndDate && changes.EndDate is null,
            ScheduledReminders: scheduledReminders,
            Emoji: changes.HasEmoji ? changes.Emoji : habit.Emoji,
            UserToday: today,
            IntervalWeeks: changes.HasIntervalWeeks ? changes.IntervalWeeks : habit.IntervalWeeks), changes);
    }
    private static HabitUpdateParams ApplyExtendedFields(HabitUpdateParams update, BulkHabitChanges changes) => update with
    {
        DueEndTime = changes.HasDueEndTime ? changes.DueEndTime : update.DueEndTime,
        IsGeneral = changes.HasIsGeneral ? changes.IsGeneral : null,
        SlipAlertEnabled = changes.HasSlipAlertEnabled ? changes.SlipAlertEnabled : null,
        RelativeReminders = changes.HasRelativeReminders ? changes.RelativeReminders ?? [] : null
    };

}
