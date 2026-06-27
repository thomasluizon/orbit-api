using FluentValidation;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class SetHandleCommandValidator : AbstractValidator<SetHandleCommand>
{
    public SetHandleCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Handle)
            .NotEmpty()
            .Matches("^[A-Za-z0-9_]{3,20}$")
            .WithMessage("Handle must be 3-20 characters using only letters, numbers, or underscores.");
    }
}
