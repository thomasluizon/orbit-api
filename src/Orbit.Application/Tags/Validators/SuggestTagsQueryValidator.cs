using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Tags.Queries;

namespace Orbit.Application.Tags.Validators;

public class SuggestTagsQueryValidator : AbstractValidator<SuggestTagsQuery>
{
    public SuggestTagsQueryValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxHabitTitleLength);

        RuleFor(x => x.Description)
            .MaximumLength(AppConstants.MaxHabitDescriptionLength);

        RuleFor(x => x.Language)
            .NotEmpty()
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => AppConstants.SupportedLanguages.Contains(lang))
            .WithMessage($"Language must be one of: {string.Join(", ", AppConstants.SupportedLanguages)}");
    }
}
