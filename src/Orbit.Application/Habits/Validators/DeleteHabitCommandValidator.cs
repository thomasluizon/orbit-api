using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class DeleteHabitCommandValidator : AbstractValidator<DeleteHabitCommand>
{
    public DeleteHabitCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.HabitId).NotEmpty();
    }
}
