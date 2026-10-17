using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class CreateSubHabitCommandValidator : AbstractValidator<CreateSubHabitCommand>
{
    public CreateSubHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.ParentHabitId)
            .NotEmpty();

        SharedHabitRules.AddTitleRules(
            RuleFor(x => x.Title),
            requiredCode: ValidationErrorCodes.SubHabitTitleRequired,
            maximumLengthCode: ValidationErrorCodes.SubHabitTitleLength);

        SharedHabitRules.AddDescriptionRules(RuleFor(x => x.Description), isChild: true);

        SharedHabitRules.AddEmojiRules(RuleFor(x => x.Emoji), isChild: true);

        SharedHabitRules.AddChecklistItemRules(RuleFor(x => x.Options != null ? x.Options.ChecklistItems : null));

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .WithFieldCopy(ValidationCopyKeys.FrequencyPositive)
            .When(x => x.FrequencyQuantity is not null);

        SharedHabitRules.AddIntervalWeeksRules(RuleFor(x => x.IntervalWeeks));

        SharedHabitRules.AddDaysRules(this,
            x => x.Options != null ? x.Options.Days : null,
            x => x.FrequencyQuantity,
            x => x.FrequencyUnit,
            x => x.Options != null && x.Options.IsFlexible);

        SharedHabitRules.AddScheduledReminderRules(RuleFor(x => x.Options != null ? x.Options.ScheduledReminders : null));

        SharedHabitRules.AddReminderTimesRules(RuleFor(x => x.Options != null ? x.Options.ReminderTimes : null));
        SharedHabitRules.AddRelativeReminderRules(RuleFor(x => x.Options != null ? x.Options.RelativeReminders : null));

        RuleFor(x => x.TagIds)
            .Must(tags => tags is null || tags.Count <= AppConstants.MaxTagsPerHabit)
            .WithCopy(ValidationErrorCodes.HabitTagLimit);
    }
}
