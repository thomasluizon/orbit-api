using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Commands;

public record CreateCheckoutCommand(Guid UserId, string Interval, string? CountryCode, string? IpAddress) : IRequest<Result<CheckoutResponse>>;

public partial class CreateCheckoutCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IGeoLocationService geoLocationService,
    IOptions<StripeSettings> stripeSettings,
    IBillingService billingService,
    ILogger<CreateCheckoutCommandHandler> logger) : IRequestHandler<CreateCheckoutCommand, Result<CheckoutResponse>>
{
    private readonly StripeSettings _settings = stripeSettings.Value;

    public async Task<Result<CheckoutResponse>> Handle(CreateCheckoutCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);
        if (user is null)
            return Result.Failure<CheckoutResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var countryCode = await SubscriptionPricingCountryResolver.ResolveCountryCodeAsync(
            user,
            request.CountryCode,
            request.IpAddress,
            geoLocationService,
            cancellationToken);
        var isBrazil = countryCode == "BR";

        var allowedIntervals = new[] { "monthly", "yearly" };
        var interval = request.Interval?.ToLower();
        if (string.IsNullOrEmpty(interval) || !allowedIntervals.Contains(interval))
            return Result.Failure<CheckoutResponse>(ErrorMessages.InvalidBillingInterval, ErrorCodes.InvalidBillingInterval);

        var priceId = (interval, isBrazil) switch
        {
            ("yearly", true) => _settings.YearlyPriceIdBrl,
            ("yearly", false) => _settings.YearlyPriceIdUsd,
            ("monthly", true) => _settings.MonthlyPriceIdBrl,
            ("monthly", false) => _settings.MonthlyPriceIdUsd,
            _ => _settings.MonthlyPriceIdBrl
        };

        try
        {
            if (string.IsNullOrEmpty(user.StripeCustomerId))
            {
                var customerId = await billingService.CreateCustomerAsync(
                    user.Email, user.Name, request.UserId, cancellationToken);
                user.SetStripeCustomerId(customerId);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            if (!string.IsNullOrEmpty(user.ReferralCouponId))
            {
                LogApplyingReferralCoupon(logger, user.ReferralCouponId, request.UserId);
            }

            var url = await billingService.CreateCheckoutSessionAsync(
                user.StripeCustomerId!,
                priceId,
                _settings.SuccessUrl,
                _settings.CancelUrl,
                request.UserId,
                user.ReferralCouponId,
                cancellationToken);

            LogCheckoutCreated(logger, request.UserId, priceId, countryCode);
            return Result.Success(new CheckoutResponse(url));
        }
        catch (BillingProviderException ex)
        {
            LogStripeCheckoutError(logger, ex, request.UserId);
            return Result.Failure<CheckoutResponse>("Payment service temporarily unavailable");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Applying referral coupon {CouponId} to checkout for user {UserId}")]
    private static partial void LogApplyingReferralCoupon(ILogger logger, string? couponId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Checkout created for user {UserId} price={PriceId} country={Country}")]
    private static partial void LogCheckoutCreated(ILogger logger, Guid userId, string priceId, string? country);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Billing provider error during checkout for user {UserId}")]
    private static partial void LogStripeCheckoutError(ILogger logger, Exception ex, Guid userId);
}
