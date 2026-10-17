using FluentValidation;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.Auth.Validators;

public class VerifyCodeCommandValidator : AbstractValidator<VerifyCodeCommand>
{
    public VerifyCodeCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .WithFieldCopy(ValidationCopyKeys.EmailRequired)
            .EmailAddress()
            .WithFieldCopy(ValidationCopyKeys.EmailFormat);

        RuleFor(x => x.Code)
            .NotEmpty()
            .WithFieldCopy(ValidationErrorCodes.VerificationCodeFormat)
            .Length(6)
            .WithFieldCopy(ValidationErrorCodes.VerificationCodeFormat)
            .Matches(@"^\d{6}$")
            .WithCopy(ValidationErrorCodes.VerificationCodeFormat);

        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang))
            .WithCopy(ValidationErrorCodes.LanguageSupported);
    }
}
