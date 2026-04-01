using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class MoveHabitParentCommandValidator : AbstractValidator<MoveHabitParentCommand>
{
    public MoveHabitParentCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();
    }
}
