using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class ReadHabitPhraseCommandValidator : AbstractValidator<ReadHabitPhraseCommand>
{
    public ReadHabitPhraseCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        SharedHabitRules.AddTitleRules(RuleFor(x => x.Phrase));

        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang))
            .WithCopy(ValidationErrorCodes.LanguageSupported);
    }
}
