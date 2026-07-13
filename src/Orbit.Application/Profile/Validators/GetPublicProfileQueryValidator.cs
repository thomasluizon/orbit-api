using FluentValidation;
using Orbit.Application.Profile.Queries;

namespace Orbit.Application.Profile.Validators;

public class GetPublicProfileQueryValidator : AbstractValidator<GetPublicProfileQuery>
{
    public GetPublicProfileQueryValidator()
    {
        RuleFor(x => x.Slug).NotEmpty();
    }
}
