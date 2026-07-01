using FluentValidation;
using Orbit.Application.Challenges.Queries;

namespace Orbit.Application.Challenges.Validators;

public class GetUserChallengesQueryValidator : AbstractValidator<GetUserChallengesQuery>
{
    public GetUserChallengesQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
