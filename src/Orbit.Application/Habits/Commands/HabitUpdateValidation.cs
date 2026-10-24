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
            IsGeneral: update.Schedule == HabitSchedule.General, ClearEndDate: update.ClearEndDate, Emoji: update.Emoji,
            IntervalWeeks: update.IntervalWeeks, StartDate: update.StartDate,
            Options: new UpdateHabitCommandOptions(update.Days, update.DueTime, update.DueEndTime,
                update.ReminderEnabled, update.ReminderTimes, update.SlipAlertEnabled, update.ChecklistItems,
                update.ScheduledReminders, update.EndDate, update.Schedule == HabitSchedule.Flexible, update.RelativeReminders));

    public static async Task<Result> ValidateAsync(Guid userId, Habit habit, HabitUpdateParams update,
        IGenericRepository<Habit> repository, CancellationToken ct, IReadOnlyList<Habit>? related = null)
    {
        var command = ToCommand(userId, habit.Id, update);
        var validation = await new UpdateHabitCommandValidator(repository).ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result.Failure(validation.ToString());
        return update.Schedule != habit.Schedule || update.ResetCompletion
            ? ValidateGeneral(habit, update.Schedule == HabitSchedule.General, related ?? await LoadRelatedAsync([habit], userId, repository, ct))
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

    public static Result<HabitUpdateParams> ResolveSingleUpdate(Habit habit, BulkHabitChanges changes, DateOnly today) =>
        ResolveUpdate(habit, changes, today, single: true);

    public static Result<HabitUpdateParams> ResolveUpdate(Habit habit, BulkHabitChanges changes, DateOnly today, bool single = false)
    {
        var dueTime = changes.HasDueTime ? changes.DueTime : habit.DueTime;
        var (reminderTimes, scheduledReminders) = ReminderStoreNormalizer.NormalizeForUpdate(
            dueTime,
            habit.DueTime,
            changes.HasReminderTimes ? changes.ReminderTimes?.ToList() ?? [] : null,
            changes.HasScheduledReminders ? changes.ScheduledReminders?.ToList() ?? [] : null,
            habit.ReminderTimes,
            habit.ScheduledReminders);

        var (frequencyUnit, frequencyQuantity, intervalWeeks, days) = ResolveCadence(habit, changes, single);
        var schedule = LegacyHabitSchedule.Resolve(
            changes.HasIsGeneral ? changes.IsGeneral : habit.Schedule == HabitSchedule.General,
            changes.HasIsFlexible ? changes.IsFlexible : habit.Schedule == HabitSchedule.Flexible,
            frequencyUnit, frequencyQuantity, changes.HasIsBadHabit ? changes.IsBadHabit : habit.IsBadHabit,
            days, intervalWeeks);
        if (schedule.IsFailure)
            return schedule.PropagateError<HabitUpdateParams>();

        return Result.Success(ApplyExtendedFields(new HabitUpdateParams(schedule.Value,
            changes.HasTitle ? changes.Title ?? habit.Title : habit.Title,
            changes.HasDescription ? changes.Description : habit.Description,
            frequencyUnit,
            frequencyQuantity,
            days,
            changes.HasIsBadHabit ? changes.IsBadHabit : habit.IsBadHabit,
            changes.HasDueDate ? changes.DueDate : habit.NextDueDate,
            DueTime: dueTime,
            DueEndTime: habit.DueEndTime,
            ReminderEnabled: changes.HasReminderEnabled ? changes.ReminderEnabled : null,
            ReminderTimes: reminderTimes,
            ChecklistItems: changes.HasChecklistItems ? changes.ChecklistItems : null,
            EndDate: changes.HasEndDate ? changes.EndDate : null,
            ClearEndDate: changes.HasEndDate && changes.EndDate is null,
            ScheduledReminders: scheduledReminders,
            Emoji: changes.HasEmoji ? changes.Emoji : habit.Emoji,
            UserToday: today,
            IntervalWeeks: intervalWeeks), changes));
    }
    private static (FrequencyUnit? Unit, int? Quantity, int? Interval, IReadOnlyList<DayOfWeek>? Days)
        ResolveCadence(Habit habit, BulkHabitChanges changes, bool single)
    {
        var frequencyUnit = changes.HasFrequencyUnit ? changes.FrequencyUnit : habit.FrequencyUnit;
        var frequencyQuantity = changes.HasFrequencyQuantity
            ? single ? changes.FrequencyQuantity ?? habit.FrequencyQuantity : changes.FrequencyQuantity : habit.FrequencyQuantity;
        var intervalWeeks = changes.HasIntervalWeeks
            ? single ? changes.IntervalWeeks ?? habit.IntervalWeeks : changes.IntervalWeeks : habit.IntervalWeeks;
        var days = !single && changes.HasIsFlexible && changes.IsFlexible ? []
            : changes.HasDays ? changes.Days : habit.Days.ToList();
        return (frequencyUnit, frequencyQuantity, intervalWeeks, days);
    }

    private static HabitUpdateParams ApplyExtendedFields(HabitUpdateParams update, BulkHabitChanges changes) => update with
    {
        DueEndTime = changes.HasDueEndTime ? changes.DueEndTime : update.DueEndTime,
        ResetCompletion = changes.HasIsGeneral && changes.IsGeneral,
        SlipAlertEnabled = changes.HasSlipAlertEnabled ? changes.SlipAlertEnabled : null,
        RelativeReminders = changes.HasRelativeReminders ? changes.RelativeReminders ?? [] : null,
        StartDate = changes.HasStartDate ? changes.StartDate : null
    };

}
