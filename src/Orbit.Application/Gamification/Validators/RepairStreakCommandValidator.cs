using FluentValidation;
using Orbit.Application.Gamification.Commands;

namespace Orbit.Application.Gamification.Validators;

public class RepairStreakCommandValidator : AbstractValidator<RepairStreakCommand>
{
    public RepairStreakCommandValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
    }
}
