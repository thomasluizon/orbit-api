using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class SuggestHabitSetupCommandValidator : AbstractValidator<SuggestHabitSetupCommand>
{
    public SuggestHabitSetupCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        SharedHabitRules.AddTitleRules(RuleFor(x => x.Title));

        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang))
            .WithMessage($"Language must be one of: {string.Join(", ", AppConstants.SupportedLanguages)}");
    }
}
