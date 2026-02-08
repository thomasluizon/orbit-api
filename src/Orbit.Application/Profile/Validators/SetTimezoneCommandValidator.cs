using FluentValidation;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class SetTimezoneCommandValidator : AbstractValidator<SetTimezoneCommand>
{
    public SetTimezoneCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.TimeZone)
            .NotEmpty()
            .MaximumLength(100);
    }
}
