using FluentValidation;
using Orbit.Application.Waitlist.Commands;

namespace Orbit.Application.Waitlist.Validators;

public class JoinWaitlistCommandValidator : AbstractValidator<JoinWaitlistCommand>
{
    private static readonly string[] SupportedLanguages = ["en", "pt-BR"];

    public JoinWaitlistCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .MaximumLength(254)
            .EmailAddress();

        RuleFor(x => x.Language)
            .Must(language => SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Language must be one of: en, pt-BR.");
    }
}
