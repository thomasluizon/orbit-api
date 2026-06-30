using FluentValidation;
using Orbit.Application.Accountability.Queries;

namespace Orbit.Application.Accountability.Validators;

public class GetAccountabilityCheckInsQueryValidator : AbstractValidator<GetAccountabilityCheckInsQuery>
{
    public GetAccountabilityCheckInsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PairId).NotEmpty();
    }
}
