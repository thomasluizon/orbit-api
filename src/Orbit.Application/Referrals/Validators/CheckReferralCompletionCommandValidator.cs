using FluentValidation;
using Orbit.Application.Referrals.Commands;

namespace Orbit.Application.Referrals.Validators;

public class CheckReferralCompletionCommandValidator : AbstractValidator<CheckReferralCompletionCommand>
{
    public CheckReferralCompletionCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
