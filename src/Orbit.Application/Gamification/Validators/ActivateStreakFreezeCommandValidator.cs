using FluentValidation;
using Orbit.Application.Gamification.Commands;

namespace Orbit.Application.Gamification.Validators;

public class ActivateStreakFreezeCommandValidator : AbstractValidator<ActivateStreakFreezeCommand>
{
    public ActivateStreakFreezeCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
