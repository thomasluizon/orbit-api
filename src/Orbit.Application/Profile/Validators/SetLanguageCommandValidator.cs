using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class SetLanguageCommandValidator : AbstractValidator<SetLanguageCommand>
{
    public SetLanguageCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang))
            .WithMessage($"Language must be one of: {string.Join(", ", AppConstants.SupportedLanguages)}");
    }
}
