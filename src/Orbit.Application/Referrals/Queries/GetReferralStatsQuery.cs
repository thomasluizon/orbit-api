using MediatR;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Referrals.Queries;

public record ReferralStatsResponse(
    string? ReferralCode,
    string? ReferralLink,
    int SuccessfulReferrals,
    int PendingReferrals,
    int MaxReferrals,
    string RewardType,
    int DiscountPercent);

public record GetReferralStatsQuery(Guid UserId) : IRequest<Result<ReferralStatsResponse>>;

public class GetReferralStatsQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Referral> referralRepository,
    IAppConfigService appConfigService,
    IOptions<FrontendSettings> frontendSettings) : IRequestHandler<GetReferralStatsQuery, Result<ReferralStatsResponse>>
{
    public async Task<Result<ReferralStatsResponse>> Handle(GetReferralStatsQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<ReferralStatsResponse>(ErrorMessages.UserNotFound);

        var referrals = await referralRepository.FindAsync(
            r => r.ReferrerId == request.UserId,
            cancellationToken: cancellationToken);

        var successful = referrals.Count(r => r.Status is ReferralStatus.Completed or ReferralStatus.Rewarded);
        var pending = referrals.Count(r => r.Status == ReferralStatus.Pending);

        var maxReferrals = await appConfigService.GetAsync(
            "MaxReferrals", AppConstants.DefaultMaxReferrals, cancellationToken);

        var referralLink = user.ReferralCode is not null
            ? $"{frontendSettings.Value.BaseUrl}/r/{user.ReferralCode}"
            : null;

        return Result.Success(new ReferralStatsResponse(
            user.ReferralCode,
            referralLink,
            successful,
            pending,
            maxReferrals,
            "discount",
            AppConstants.ReferralDiscountPercent));
    }
}
