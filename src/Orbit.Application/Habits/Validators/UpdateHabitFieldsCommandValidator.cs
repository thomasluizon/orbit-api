using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public sealed class UpdateHabitFieldsCommandValidator : AbstractValidator<UpdateHabitFieldsCommand>
{
    public UpdateHabitFieldsCommandValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.HabitId).NotEmpty();
        RuleFor(command => command.Changes).NotNull();
    }
}
