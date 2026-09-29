using FluentValidation;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class SetClockFormatCommandValidator : AbstractValidator<SetClockFormatCommand>
{
    public SetClockFormatCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
