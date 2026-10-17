using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public sealed class UndoSkipHabitCommandValidator : AbstractValidator<UndoSkipHabitCommand>
{
    public UndoSkipHabitCommandValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.HabitId).NotEmpty();
        RuleFor(command => command.SkipId).NotEmpty();
    }
}
