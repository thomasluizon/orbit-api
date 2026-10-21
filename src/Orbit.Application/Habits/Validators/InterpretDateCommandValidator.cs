using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Validators;

public sealed class InterpretDateCommandValidator : AbstractValidator<InterpretDateCommand>
{
    public InterpretDateCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Phrase).NotEmpty().MaximumLength(DateInterpretationInput.MaxPhraseLength)
            .Must(value => value is null || !value.Any(char.IsControl));
        RuleFor(x => x.Language).Must(value => value is "en" or "pt-BR")
            .WithCopy(ValidationErrorCodes.LanguageSupported);
    }
}
