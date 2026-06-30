using FluentValidation;
using Orbit.Application.Accountability.Queries;

namespace Orbit.Application.Accountability.Validators;

public class GetAccountabilityPairsQueryValidator : AbstractValidator<GetAccountabilityPairsQuery>
{
    public GetAccountabilityPairsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
