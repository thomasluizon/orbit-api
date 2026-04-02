using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Goals.Queries;

namespace Orbit.Application.Goals.Validators;

public class GetGoalsQueryValidator : AbstractValidator<GetGoalsQuery>
{
    public GetGoalsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, AppConstants.MaxPageSize);
    }
}
