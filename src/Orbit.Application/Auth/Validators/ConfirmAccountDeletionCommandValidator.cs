using Orbit.Application.Common;
using FluentValidation;
using Orbit.Application.Auth.Commands;

namespace Orbit.Application.Auth.Validators;

public class ConfirmAccountDeletionCommandValidator : AbstractValidator<ConfirmAccountDeletionCommand>
{
    public ConfirmAccountDeletionCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Code)
            .NotEmpty()
            .WithFieldCopy(ValidationErrorCodes.VerificationCodeFormat)
            .Length(6)
            .WithFieldCopy(ValidationErrorCodes.VerificationCodeFormat)
            .Matches(@"^\d{6}$")
            .WithCopy(ValidationErrorCodes.VerificationCodeFormat);
    }
}
