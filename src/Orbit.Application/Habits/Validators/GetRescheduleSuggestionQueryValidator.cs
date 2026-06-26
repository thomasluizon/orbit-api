using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetRescheduleSuggestionQueryValidator : AbstractValidator<GetRescheduleSuggestionQuery>
{
    public GetRescheduleSuggestionQueryValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.Language)
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => string.IsNullOrEmpty(lang) || AppConstants.SupportedLanguages.Contains(lang))
            .WithMessage($"Language must be one of: {string.Join(", ", AppConstants.SupportedLanguages)}");
    }
}
