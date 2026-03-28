using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Infrastructure.Services;

public class StripeCouponRewardService(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    ILogger<StripeCouponRewardService> logger) : IReferralRewardService
{
    private const string ProductId = "prod_UBUPrTlZg8chuk";

    public async Task<string> CreateReferralCouponAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);

        if (user is null)
            throw new InvalidOperationException($"User {userId} not found for coupon creation");

        // Ensure user has a Stripe customer
        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            var customerService = new CustomerService();
            var customer = await customerService.CreateAsync(new CustomerCreateOptions
            {
                Email = user.Email,
                Name = user.Name,
                Metadata = new Dictionary<string, string> { { "userId", userId.ToString() } }
            }, cancellationToken: cancellationToken);
            user.SetStripeCustomerId(customer.Id);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var couponService = new CouponService();
        var coupon = await couponService.CreateAsync(new CouponCreateOptions
        {
            PercentOff = AppConstants.ReferralDiscountPercent,
            Duration = "once",
            MaxRedemptions = 1,
            Name = "Referral Discount",
            AppliesTo = new CouponAppliesToOptions
            {
                Products = [ProductId]
            }
        }, cancellationToken: cancellationToken);

        var promoCodeService = new PromotionCodeService();
        var promoCode = await promoCodeService.CreateAsync(new PromotionCodeCreateOptions
        {
            Promotion = new PromotionCodePromotionOptions { Coupon = coupon.Id },
            Customer = user.StripeCustomerId,
            MaxRedemptions = 1
        }, cancellationToken: cancellationToken);

        logger.LogInformation(
            "Created referral coupon for user {UserId}: coupon={CouponId}, promoCode={PromoCodeId}",
            userId, coupon.Id, promoCode.Id);

        return promoCode.Id;
    }

    public async Task<string?> GetUserPromotionCodeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        return user?.ReferralCouponId;
    }
}
