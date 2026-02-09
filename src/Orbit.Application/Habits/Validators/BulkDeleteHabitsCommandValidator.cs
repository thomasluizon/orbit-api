using FluentValidation;
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
            .WithMessage("HabitIds list must not be empty")
            .Must(ids => ids.Count <= 100)
            .WithMessage("Cannot delete more than 100 habits at once");

        RuleForEach(x => x.HabitIds)
            .NotEmpty()
            .WithMessage("Habit ID must not be empty");
    }
}
