using FluentValidation;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Application.Gamification.Validators;

public class GetGamificationProfileQueryValidator : AbstractValidator<GetGamificationProfileQuery>
{
    public GetGamificationProfileQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
