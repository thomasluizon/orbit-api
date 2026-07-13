using FluentValidation;
using Orbit.Application.Goals.Queries;

namespace Orbit.Application.Goals.Validators;

public class GetGoalByIdQueryValidator : AbstractValidator<GetGoalByIdQuery>
{
    public GetGoalByIdQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
    }
}
