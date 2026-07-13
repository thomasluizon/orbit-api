using FluentValidation;
using Orbit.Application.Goals.Queries;

namespace Orbit.Application.Goals.Validators;

public class GetGoalMetricsQueryValidator : AbstractValidator<GetGoalMetricsQuery>
{
    public GetGoalMetricsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
    }
}
