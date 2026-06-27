using FluentValidation;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class SetSocialOptInCommandValidator : AbstractValidator<SetSocialOptInCommand>
{
    public SetSocialOptInCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
