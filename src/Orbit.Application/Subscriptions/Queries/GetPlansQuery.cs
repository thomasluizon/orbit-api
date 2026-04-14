using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Application.Subscriptions.Queries;

public record GetPlansQuery(Guid UserId, string? CountryCode, string? IpAddress) : IRequest<Result<PlansResponse>>;

public partial class GetPlansQueryHandler(
    IGenericRepository<User> userRepository,
    IGeoLocationService geoLocationService,
    IOptions<StripeSettings> stripeSettings,
    PriceService priceService,
    CouponService couponService,
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
            var monthlyPrice = await priceService.GetAsync(monthlyPriceId, cancellationToken: cancellationToken);
            var yearlyPrice = await priceService.GetAsync(yearlyPriceId, cancellationToken: cancellationToken);

            var monthlyAmount = monthlyPrice.UnitAmount ?? 0;
            var yearlyAmount = yearlyPrice.UnitAmount ?? 0;

            var savingsPercent = monthlyAmount > 0
                ? (int)Math.Round((1 - (double)yearlyAmount / (monthlyAmount * 12)) * 100)
                : 0;

            int? couponPercentOff = null;
            if (!string.IsNullOrEmpty(user.ReferralCouponId))
            {
                try
                {
                    var coupon = await couponService.GetAsync(user.ReferralCouponId, cancellationToken: cancellationToken);
                    couponPercentOff = (int)(coupon.PercentOff ?? 0);
                }
                catch (StripeException ex)
                {
                    LogFetchReferralCouponFailed(logger, ex, user.ReferralCouponId, request.UserId);
                }
            }

            return Result.Success(new PlansResponse(
                new PlanPriceDto(monthlyAmount, currency),
                new PlanPriceDto(yearlyAmount, currency),
                savingsPercent,
                couponPercentOff,
                currency));
        }
        catch (StripeException ex)
        {
            LogFetchPlansFailed(logger, ex);
            return Result.Failure<PlansResponse>("Payment service temporarily unavailable");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch referral coupon {CouponId} for user {UserId}")]
    private static partial void LogFetchReferralCouponFailed(ILogger logger, Exception ex, string? couponId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to fetch plans from Stripe")]
    private static partial void LogFetchPlansFailed(ILogger logger, Exception ex);
}
