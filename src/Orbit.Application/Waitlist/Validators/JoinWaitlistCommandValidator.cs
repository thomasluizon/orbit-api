using Orbit.Application.Common;
using FluentValidation;
using Orbit.Application.Waitlist.Commands;

namespace Orbit.Application.Waitlist.Validators;

public class JoinWaitlistCommandValidator : AbstractValidator<JoinWaitlistCommand>
{
    public JoinWaitlistCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .MaximumLength(254)
            .EmailAddress();

        RuleFor(x => x.Language)
            .Must(language => WaitlistLanguage.TryCanonicalize(language, out _))
            .WithCopy(ValidationErrorCodes.WaitlistLanguageSupported);
    }
}
