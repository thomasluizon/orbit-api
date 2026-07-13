using FluentValidation;
using Orbit.Application.Referrals.Queries;

namespace Orbit.Application.Referrals.Validators;

public class GetReferralDashboardQueryValidator : AbstractValidator<GetReferralDashboardQuery>
{
    public GetReferralDashboardQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
