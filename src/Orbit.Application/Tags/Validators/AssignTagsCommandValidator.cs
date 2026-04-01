using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Tags.Commands;

namespace Orbit.Application.Tags.Validators;

public class AssignTagsCommandValidator : AbstractValidator<AssignTagsCommand>
{
    public AssignTagsCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.TagIds)
            .NotNull();

        RuleFor(x => x.TagIds)
            .Must(tags => tags is null || tags.Count <= AppConstants.MaxTagsPerHabit)
            .WithMessage($"A habit can have at most {AppConstants.MaxTagsPerHabit} tags");
    }
}
