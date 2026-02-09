using FluentValidation;
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
            .Must(habits => habits.Count <= 100)
            .WithMessage("Cannot create more than 100 habits at once");
    }
}
