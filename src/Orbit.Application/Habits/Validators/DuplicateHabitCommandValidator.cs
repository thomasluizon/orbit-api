using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class DuplicateHabitCommandValidator : AbstractValidator<DuplicateHabitCommand>
{
    public DuplicateHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();
    }
}
