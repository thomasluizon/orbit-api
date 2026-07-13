using FluentValidation;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Application.Gamification.Validators;

public class GetAchievementsQueryValidator : AbstractValidator<GetAchievementsQuery>
{
    public GetAchievementsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
