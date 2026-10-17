using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Tags.Queries;

namespace Orbit.Application.Tags.Validators;

public class SuggestTagsQueryValidator : AbstractValidator<SuggestTagsQuery>
{
    public SuggestTagsQueryValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        SharedHabitRules.AddTitleRules(RuleFor(x => x.Title));

        SharedHabitRules.AddDescriptionRules(RuleFor(x => x.Description));

        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang))
            .WithCopy(ValidationErrorCodes.LanguageSupported);
    }
}
