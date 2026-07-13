using FluentValidation;
using Orbit.Application.Referrals.Queries;

namespace Orbit.Application.Referrals.Validators;

public class GetReferralStatsQueryValidator : AbstractValidator<GetReferralStatsQuery>
{
    public GetReferralStatsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
