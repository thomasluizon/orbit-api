using FluentValidation;
using Orbit.Application.Goals.Queries;

namespace Orbit.Application.Goals.Validators;

public class GetGoalDetailQueryValidator : AbstractValidator<GetGoalDetailQuery>
{
    public GetGoalDetailQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
    }
}
