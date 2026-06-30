using FluentValidation;
using Orbit.Application.Challenges.Queries;

namespace Orbit.Application.Challenges.Validators;

public class GetChallengeDetailQueryValidator : AbstractValidator<GetChallengeDetailQuery>
{
    public GetChallengeDetailQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ChallengeId).NotEmpty();
    }
}
