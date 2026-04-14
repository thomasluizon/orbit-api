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
            .WithMessage("Habits list must not be empty")
            .Must(habits => habits.Count <= AppConstants.MaxBulkOperationSize)
            .WithMessage($"Cannot create more than {AppConstants.MaxBulkOperationSize} habits at once");

        RuleForEach(x => x.Habits).ChildRules(habit =>
        {
            SharedHabitRules.AddTitleRules(habit.RuleFor(h => h.Title));

            SharedHabitRules.AddIconRules(habit.RuleFor(h => h.Icon));

            habit.RuleFor(h => h.FrequencyQuantity)
                .GreaterThan(0)
                .When(h => h.FrequencyQuantity is not null);

            habit.RuleFor(h => h.FrequencyQuantity)
                .NotNull()
                .WithMessage("Frequency quantity is required when frequency unit is set")
                .When(h => h.FrequencyUnit is not null);
        });
    }
}
