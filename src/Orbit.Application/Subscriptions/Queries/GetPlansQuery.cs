using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Queries;

public record GetPlansQuery(Guid UserId, string? CountryCode, string? IpAddress) : IRequest<Result<PlansResponse>>;

public partial class GetPlansQueryHandler(
    IGenericRepository<User> userRepository,
    IGeoLocationService geoLocationService,
    IOptions<StripeSettings> stripeSettings,
    IBillingService billingService,
    ILogger<GetPlansQueryHandler> logger) : IRequestHandler<GetPlansQuery, Result<PlansResponse>>
{
    private readonly StripeSettings _settings = stripeSettings.Value;

    public async Task<Result<PlansResponse>> Handle(GetPlansQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<PlansResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var countryCode = await SubscriptionPricingCountryResolver.ResolveCountryCodeAsync(
            user,
            request.CountryCode,
            request.IpAddress,
            geoLocationService,
            cancellationToken);
        var isBrazil = countryCode == "BR";

        var monthlyPriceId = isBrazil ? _settings.MonthlyPriceIdBrl : _settings.MonthlyPriceIdUsd;
        var yearlyPriceId = isBrazil ? _settings.YearlyPriceIdBrl : _settings.YearlyPriceIdUsd;
        var currency = isBrazil ? "brl" : "usd";

        try
        {
            var monthlyAmount = await billingService.GetPriceUnitAmountAsync(monthlyPriceId, cancellationToken);
            var yearlyAmount = await billingService.GetPriceUnitAmountAsync(yearlyPriceId, cancellationToken);

            var savingsPercent = monthlyAmount > 0
                ? (int)Math.Round((1 - (double)yearlyAmount / (monthlyAmount * 12)) * 100)
                : 0;

            int? couponPercentOff = null;
            if (!string.IsNullOrEmpty(user.ReferralCouponId))
            {
                // TryGet returns null on any provider error; treat as "no discount" and log.
                couponPercentOff = await billingService.TryGetCouponPercentOffAsync(user.ReferralCouponId, cancellationToken);
                if (couponPercentOff is null)
                {
                    LogFetchReferralCouponFailed(logger, user.ReferralCouponId, request.UserId);
                }
            }

            return Result.Success(new PlansResponse(
                new PlanPriceDto(monthlyAmount, currency),
                new PlanPriceDto(yearlyAmount, currency),
                savingsPercent,
                couponPercentOff,
                currency));
        }
        catch (BillingProviderException ex)
        {
            LogFetchPlansFailed(logger, ex);
            return Result.Failure<PlansResponse>("Payment service temporarily unavailable");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch referral coupon {CouponId} for user {UserId}")]
    private static partial void LogFetchReferralCouponFailed(ILogger logger, string? couponId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to fetch plans from billing provider")]
    private static partial void LogFetchPlansFailed(ILogger logger, Exception ex);
}
