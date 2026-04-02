using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Application.Subscriptions.Queries;

public record GetPlansQuery(Guid UserId, string? IpAddress) : IRequest<Result<PlansResponse>>;

public class GetPlansQueryHandler(
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
            return Result.Failure<PlansResponse>(ErrorMessages.UserNotFound);

        var countryCode = await geoLocationService.GetCountryCodeAsync(request.IpAddress, cancellationToken);
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
                    logger.LogWarning(ex, "Failed to fetch referral coupon {CouponId} for user {UserId}", user.ReferralCouponId, request.UserId);
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
            logger.LogError(ex, "Failed to fetch plans from Stripe");
            return Result.Failure<PlansResponse>("Payment service temporarily unavailable");
        }
    }
}
