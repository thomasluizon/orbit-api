using FluentValidation;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.Auth.Validators;

public class GoogleAuthCommandValidator : AbstractValidator<GoogleAuthCommand>
{
    public GoogleAuthCommandValidator()
    {
        RuleFor(x => x.AccessToken)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.AccessTokenRequired);

        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang))
            .WithCopy(ValidationErrorCodes.LanguageSupported);
    }
}
