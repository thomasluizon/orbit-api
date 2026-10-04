using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class BulkDeleteHabitsCommandValidator : AbstractValidator<BulkDeleteHabitsCommand>
{
    public BulkDeleteHabitsCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitIds)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.BulkHabitIdsRequired)
            .Must(ids => ids.Count <= AppConstants.MaxBulkOperationSize)
            .WithCopy(ValidationErrorCodes.BulkDeleteHabitLimit);

        RuleForEach(x => x.HabitIds)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.HabitIdRequired);
    }
}
