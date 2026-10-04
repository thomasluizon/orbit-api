using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;

namespace Orbit.Application.Referrals.Validators;

public class ProcessReferralCodeCommandValidator : AbstractValidator<ProcessReferralCodeCommand>
{
    public ProcessReferralCodeCommandValidator()
    {
        RuleFor(x => x.NewUserId).NotEmpty();
        RuleFor(x => x.ReferralCode).NotEmpty().WithFieldCopy(ValidationCopyKeys.ReferralCodeRequired)
            .MaximumLength(50).WithFieldCopy(ValidationCopyKeys.ReferralCodeLength);
    }
}
