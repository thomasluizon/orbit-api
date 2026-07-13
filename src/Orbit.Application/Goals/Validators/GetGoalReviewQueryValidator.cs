using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Goals.Queries;

namespace Orbit.Application.Goals.Validators;

public class GetGoalReviewQueryValidator : AbstractValidator<GetGoalReviewQuery>
{
    public GetGoalReviewQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        RuleFor(x => x.Language)
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => string.IsNullOrEmpty(lang) || AppConstants.SupportedLanguages.Contains(lang))
            .WithMessage($"Language must be one of: {string.Join(", ", AppConstants.SupportedLanguages)}");
    }
}
