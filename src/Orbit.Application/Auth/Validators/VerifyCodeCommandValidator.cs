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
            .EmailAddress();

        RuleFor(x => x.Code)
            .NotEmpty()
            .Length(6)
            .Matches(@"^\d{6}$")
            .WithMessage("Code must be a 6-digit number");

        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang))
            .WithMessage($"Language must be one of: {string.Join(", ", AppConstants.SupportedLanguages)}");
    }
}
