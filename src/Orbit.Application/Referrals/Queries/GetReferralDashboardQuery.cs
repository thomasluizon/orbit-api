using MediatR;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Referrals.Queries;

public record ReferralDashboardResponse(
    string Code,
    string Link,
    ReferralStatsResponse Stats);

public record GetReferralDashboardQuery(Guid UserId) : IRequest<Result<ReferralDashboardResponse>>;

public class GetReferralDashboardQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Referral> referralRepository,
    IAppConfigService appConfigService,
    IOptions<FrontendSettings> frontendSettings,
    IMediator mediator) : IRequestHandler<GetReferralDashboardQuery, Result<ReferralDashboardResponse>>
{
    public async Task<Result<ReferralDashboardResponse>> Handle(GetReferralDashboardQuery request, CancellationToken cancellationToken)
    {
        // Get or create the referral code via existing command
        var codeResult = await mediator.Send(new GetOrCreateReferralCodeCommand(request.UserId), cancellationToken);
        if (!codeResult.IsSuccess)
            return Result.Failure<ReferralDashboardResponse>(codeResult.Error);

        var code = codeResult.Value;
        var link = $"{frontendSettings.Value.BaseUrl}/r/{code}";

        // Build stats inline (same logic as GetReferralStatsQueryHandler)
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<ReferralDashboardResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var referrals = await referralRepository.FindAsync(
            r => r.ReferrerId == request.UserId,
            cancellationToken: cancellationToken);

        var successful = referrals.Count(r => r.Status is ReferralStatus.Completed or ReferralStatus.Rewarded);
        var pending = referrals.Count(r => r.Status == ReferralStatus.Pending);

        var maxReferrals = await appConfigService.GetAsync(
            "MaxReferrals", AppConstants.DefaultMaxReferrals, cancellationToken);

        var stats = new ReferralStatsResponse(
            user.ReferralCode,
            link,
            successful,
            pending,
            maxReferrals,
            "discount",
            AppConstants.ReferralDiscountPercent);

        return Result.Success(new ReferralDashboardResponse(code, link, stats));
    }
}
