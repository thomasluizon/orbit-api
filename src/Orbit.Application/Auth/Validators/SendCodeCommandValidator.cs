using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Auth.Commands;

namespace Orbit.Application.Auth.Validators;

public class SendCodeCommandValidator : AbstractValidator<SendCodeCommand>
{
    public SendCodeCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .WithFieldCopy(ValidationCopyKeys.EmailRequired)
            .EmailAddress()
            .WithFieldCopy(ValidationCopyKeys.EmailFormat);
    }
}
