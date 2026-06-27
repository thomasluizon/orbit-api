using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class RestoreHabitCommandValidator : AbstractValidator<RestoreHabitCommand>
{
    public RestoreHabitCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.HabitId).NotEmpty();
    }
}
