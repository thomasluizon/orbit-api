using FluentValidation;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Application.Gamification.Validators;

public class GetStreakInfoQueryValidator : AbstractValidator<GetStreakInfoQuery>
{
    public GetStreakInfoQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
