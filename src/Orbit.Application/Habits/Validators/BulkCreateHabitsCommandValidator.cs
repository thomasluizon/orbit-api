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

        RuleForEach(x => x.Habits).ChildRules(habit =>
        {
            SharedHabitRules.AddTitleRules(habit.RuleFor(h => h.Title));

            SharedHabitRules.AddEmojiRules(habit.RuleFor(h => h.Emoji));

            habit.RuleFor(h => h.FrequencyQuantity)
                .GreaterThan(0)
                .When(h => h.FrequencyQuantity is not null);

            habit.RuleFor(h => h.FrequencyQuantity)
                .NotNull()
                .WithCopy(ValidationErrorCodes.FrequencyQuantityRequired)
                .When(h => h.FrequencyUnit is not null);

            SharedHabitRules.AddIntervalWeeksRules(habit.RuleFor(h => h.IntervalWeeks));
            SharedHabitRules.AddRelativeReminderRules(habit.RuleFor(h => h.RelativeReminders));

            habit.RuleFor(h => h.Tags)
                .Must(tags => tags!.Count <= AppConstants.MaxTagsPerHabit)
                .WithCopy(ValidationErrorCodes.BulkTagLimit)
                .When(h => h.Tags is not null);

            habit.RuleForEach(h => h.Tags)
                .MaximumLength(50)
                .When(h => h.Tags is not null);
        });
    }
}
