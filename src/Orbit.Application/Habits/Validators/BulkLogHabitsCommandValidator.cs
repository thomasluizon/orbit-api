using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class BulkLogHabitsCommandValidator : AbstractValidator<BulkLogHabitsCommand>
{
    public BulkLogHabitsCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Items)
            .NotEmpty()
            .WithMessage("Items list must not be empty");

        RuleFor(x => x.Items)
            .Must(items => items is null || items.Count <= AppConstants.MaxBulkOperationSize)
            .WithMessage($"Bulk log cannot exceed {AppConstants.MaxBulkOperationSize} items");

        RuleForEach(x => x.Items)
            .ChildRules(item =>
            {
                item.RuleFor(i => i.HabitId)
                    .NotEmpty();
            });
    }
}
