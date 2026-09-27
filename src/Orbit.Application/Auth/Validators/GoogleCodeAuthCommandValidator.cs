using FluentValidation;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.Auth.Validators;

public sealed class GoogleCodeAuthCommandValidator : AbstractValidator<GoogleCodeAuthCommand>
{
    public GoogleCodeAuthCommandValidator()
    {
        RuleFor(x => x.Code).NotEmpty();
        RuleFor(x => x.CodeVerifier).NotEmpty();
        RuleFor(x => x.RedirectUri).NotEmpty();
        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang));
    }
}
