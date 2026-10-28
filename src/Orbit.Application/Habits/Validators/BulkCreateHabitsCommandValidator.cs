using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class BulkCreateHabitsCommandValidator : AbstractValidator<BulkCreateHabitsCommand>
{
    public BulkCreateHabitsCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Habits)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.BulkHabitsRequired)
            .Must(habits => habits.Count <= AppConstants.MaxBulkOperationSize)
            .WithCopy(ValidationErrorCodes.BulkCreateLimit);

        RuleForEach(x => x.Habits).SetValidator(new BulkHabitItemValidator(isRoot: true));
    }
}

internal sealed class BulkHabitItemValidator : AbstractValidator<BulkHabitItem>
{
    internal BulkHabitItemValidator(bool isRoot)
    {
        SharedHabitRules.AddTitleRules(RuleFor(h => h.Title));
        SharedHabitRules.AddRepeatReminderRules(RuleFor(h => h.RepeatReminder));
        SharedHabitRules.AddPolarityRules(this, x => x.Title, x => x.IsBadHabit);
        SharedHabitRules.AddDescriptionRules(RuleFor(h => h.Description), isChild: !isRoot);
        SharedHabitRules.AddEmojiRules(RuleFor(h => h.Emoji));
        SharedHabitRules.AddChecklistItemRules(RuleFor(h => h.ChecklistItems));

        RuleFor(h => h.FrequencyQuantity)
            .GreaterThan(0)
            .WithFieldCopy(ValidationCopyKeys.FrequencyPositive)
            .When(h => h.FrequencyQuantity is not null);

        if (isRoot)
            RuleFor(h => h.FrequencyQuantity)
                .NotNull()
                .WithCopy(ValidationErrorCodes.FrequencyQuantityRequired)
                .When(h => h.FrequencyUnit is not null);

        SharedHabitRules.AddIntervalWeeksRules(RuleFor(h => h.IntervalWeeks));
        SharedHabitRules.AddRelativeReminderRules(RuleFor(h => h.RelativeReminders));

        RuleFor(h => h.Tags)
            .Must(tags => tags!.Count <= AppConstants.MaxTagsPerHabit)
            .WithCopy(ValidationErrorCodes.BulkTagLimit)
            .When(h => h.Tags is not null);

        RuleForEach(h => h.Tags)
            .MaximumLength(50)
            .WithFieldCopy(ValidationCopyKeys.TagNameLength)
            .When(h => h.Tags is not null);
    }
}
