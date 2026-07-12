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
            .WithMessage("Access token is required.");

        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang))
            .WithMessage($"Language must be one of: {string.Join(", ", AppConstants.SupportedLanguages)}");
    }
}
