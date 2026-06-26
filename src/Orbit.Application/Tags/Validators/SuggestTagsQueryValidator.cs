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
    }
}
