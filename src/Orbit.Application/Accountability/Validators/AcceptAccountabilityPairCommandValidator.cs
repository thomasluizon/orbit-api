using FluentValidation;
using Orbit.Application.Accountability.Commands;

namespace Orbit.Application.Accountability.Validators;

public class AcceptAccountabilityPairCommandValidator : AbstractValidator<AcceptAccountabilityPairCommand>
{
    public AcceptAccountabilityPairCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PairId).NotEmpty();
        AccountabilityHabitRules.AddHabitIdsRules(RuleFor(x => x.HabitIds));
    }
}
