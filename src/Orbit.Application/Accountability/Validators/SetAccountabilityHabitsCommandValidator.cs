using FluentValidation;
using Orbit.Application.Accountability.Commands;

namespace Orbit.Application.Accountability.Validators;

public class SetAccountabilityHabitsCommandValidator : AbstractValidator<SetAccountabilityHabitsCommand>
{
    public SetAccountabilityHabitsCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PairId).NotEmpty();
        AccountabilityHabitRules.AddHabitIdsRules(RuleFor(x => x.HabitIds));
    }
}
