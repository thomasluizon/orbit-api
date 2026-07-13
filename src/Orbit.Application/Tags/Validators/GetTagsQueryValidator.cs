using FluentValidation;
using Orbit.Application.Tags.Queries;

namespace Orbit.Application.Tags.Validators;

public class GetTagsQueryValidator : AbstractValidator<GetTagsQuery>
{
    public GetTagsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
