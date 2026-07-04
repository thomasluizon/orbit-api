using FluentValidation;
using Orbit.Application.Accountability.Commands;

namespace Orbit.Application.Accountability.Validators;

public class InviteAccountabilityBuddyCommandValidator : AbstractValidator<InviteAccountabilityBuddyCommand>
{
    public InviteAccountabilityBuddyCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.BuddyUserId).NotEmpty().NotEqual(x => x.UserId);
        RuleFor(x => x.Cadence).IsInEnum();
        AccountabilityHabitRules.AddHabitIdsRules(RuleFor(x => x.HabitIds));
    }
}
