using FluentValidation;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class SetMascotMotionCommandValidator : AbstractValidator<SetMascotMotionCommand>
{
    public SetMascotMotionCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
