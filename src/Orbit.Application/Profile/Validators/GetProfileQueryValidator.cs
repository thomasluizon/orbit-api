using FluentValidation;
using Orbit.Application.Profile.Queries;

namespace Orbit.Application.Profile.Validators;

public class GetProfileQueryValidator : AbstractValidator<GetProfileQuery>
{
    public GetProfileQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
