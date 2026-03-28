using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Enums;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration; // TODO (Issue 51): StripeSettings is an infrastructure concern; consider moving to Application layer config or abstracting behind an interface
using Orbit.Infrastructure.Services; // TODO (Issue 51): IGeoLocationService is defined in Infrastructure; consider moving its interface to Domain/Application to remove the layer violation
using Stripe;
using Stripe.Checkout;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SubscriptionController(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IPayGateService payGate,
    IOptions<StripeSettings> stripeSettings,
    IGeoLocationService geoLocationService,
    ILogger<SubscriptionController> logger) : ControllerBase
{
    private readonly StripeSettings _settings = stripeSettings.Value;

    public record CreateCheckoutRequest(string Interval);
    public record CheckoutResponse(string Url);
    public record PortalResponse(string Url);
    public record SubscriptionStatusResponse(
        string Plan,
        bool HasProAccess,
        bool IsTrialActive,
        DateTime? TrialEndsAt,
        DateTime? PlanExpiresAt,
        int AiMessagesUsed,
        int AiMessagesLimit,
        bool IsLifetimePro,
        string? SubscriptionInterval);

    [HttpPost("checkout")]
    public async Task<IActionResult> CreateCheckout(
        [FromBody] CreateCheckoutRequest request,
        CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
        if (user is null) return NotFound(new { error = ErrorMessages.UserNotFound });

        // Prefer X-Forwarded-For (set by BFF/reverse proxy) over direct connection IP
        var forwardedFor = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var ip = forwardedFor?.Split(',')[0].Trim()
                 ?? HttpContext.Connection.RemoteIpAddress?.ToString();
        var countryCode = await geoLocationService.GetCountryCodeAsync(ip, ct);
        var isBrazil = countryCode == "BR";

        var allowedIntervals = new[] { "monthly", "yearly" };
        var interval = request.Interval?.ToLower();
        if (string.IsNullOrEmpty(interval) || !allowedIntervals.Contains(interval))
        {
            return BadRequest(new { error = "Invalid billing interval" });
        }

        var priceId = (interval, isBrazil) switch
        {
            ("yearly", true) => _settings.YearlyPriceIdBrl,
            ("yearly", false) => _settings.YearlyPriceIdUsd,
            ("monthly", true) => _settings.MonthlyPriceIdBrl,
            ("monthly", false) => _settings.MonthlyPriceIdUsd,
            _ => _settings.MonthlyPriceIdBrl // unreachable after validation
        };

        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            var customerService = new CustomerService();
            var customer = await customerService.CreateAsync(new CustomerCreateOptions
            {
                Email = user.Email,
                Name = user.Name,
                Metadata = new Dictionary<string, string> { { "userId", userId.ToString() } }
            }, cancellationToken: ct);
            user.SetStripeCustomerId(customer.Id);
            await unitOfWork.SaveChangesAsync(ct);
        }

        var sessionOptions = new SessionCreateOptions
        {
            Customer = user.StripeCustomerId,
            Mode = "subscription",
            LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            SuccessUrl = _settings.SuccessUrl,
            CancelUrl = _settings.CancelUrl,
            Metadata = new Dictionary<string, string> { { "userId", userId.ToString() } }
        };

        // Apply referral discount coupon if user has one, otherwise allow manual promo codes
        if (!string.IsNullOrEmpty(user.ReferralCouponId))
        {
            sessionOptions.Discounts = [new SessionDiscountOptions { Coupon = user.ReferralCouponId }];
            logger.LogInformation("Applying referral coupon {CouponId} to checkout for user {UserId}", user.ReferralCouponId, userId);
        }
        else
        {
            sessionOptions.AllowPromotionCodes = true;
        }

        var sessionService = new SessionService();
        var session = await sessionService.CreateAsync(sessionOptions, cancellationToken: ct);

        logger.LogInformation("Checkout created for user {UserId} price={PriceId} country={Country}", userId, priceId, countryCode);
        return Ok(new CheckoutResponse(session.Url));
    }

    [HttpPost("portal")]
    public async Task<IActionResult> CreatePortal(CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null) return NotFound(new { error = ErrorMessages.UserNotFound });

        if (string.IsNullOrEmpty(user.StripeCustomerId))
            return BadRequest(new { error = "No subscription found" });

        var portalService = new Stripe.BillingPortal.SessionService();
        var session = await portalService.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = user.StripeCustomerId,
            ReturnUrl = _settings.SuccessUrl.Replace("?subscription=success", "")
        }, cancellationToken: ct);

        return Ok(new PortalResponse(session.Url));
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null) return NotFound(new { error = ErrorMessages.UserNotFound });

        return Ok(new SubscriptionStatusResponse(
            user.HasProAccess ? "pro" : "free",
            user.HasProAccess,
            user.IsTrialActive,
            user.TrialEndsAt,
            user.PlanExpiresAt,
            user.AiMessagesUsedThisMonth,
            await payGate.GetAiMessageLimit(user.Id, ct),
            user.IsLifetimePro,
            user.SubscriptionInterval?.ToString().ToLowerInvariant()));
    }

    // TODO (Issue 50): The business logic in HandleWebhook (subscription activation, renewal,
    // cancellation) should be extracted into dedicated MediatR commands:
    //   - ActivateSubscriptionCommand (checkout.session.completed)
    //   - RenewSubscriptionCommand    (invoice.paid)
    //   - CancelSubscriptionCommand   (customer.subscription.deleted / updated)
    // This would make each event handler independently testable and remove direct
    // repository access from the controller. Defer until a dedicated billing refactor.
    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook(CancellationToken ct)
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(ct);
        logger.LogInformation("Stripe webhook received, body length: {Length}", json.Length);

        if (string.IsNullOrEmpty(_settings.WebhookSecret))
        {
            logger.LogCritical("Stripe WebhookSecret is not configured -- rejecting webhook");
            return StatusCode(500);
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _settings.WebhookSecret,
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe webhook signature verification failed");
            return BadRequest();
        }

        logger.LogInformation("Stripe event type: {Type}, id: {Id}", stripeEvent.Type, stripeEvent.Id);

        try
        {
            switch (stripeEvent.Type)
            {
                case "checkout.session.completed":
                {
                    var session = stripeEvent.Data.Object as Session;
                    logger.LogInformation("Checkout session: id={Id}, subscription={SubId}, metadata keys={Keys}",
                        session?.Id, session?.SubscriptionId ?? session?.Subscription?.Id,
                        session?.Metadata != null ? string.Join(",", session.Metadata.Keys) : "null");

                    var subscriptionId = session?.SubscriptionId ?? session?.Subscription?.Id;

                    if (session?.Metadata?.TryGetValue("userId", out var userIdStr) == true
                        && Guid.TryParse(userIdStr, out var uid))
                    {
                        var user = await userRepository.FindOneTrackedAsync(u => u.Id == uid, cancellationToken: ct);
                        logger.LogInformation("User found: {Found}, subscriptionId: {SubId}", user is not null, subscriptionId);

                        if (user is not null && subscriptionId is not null)
                        {
                            // Fetch subscription to get period end
                            var subService = new SubscriptionService();
                            var subscription = await subService.GetAsync(subscriptionId, cancellationToken: ct);
                            var periodEnd = subscription.Items?.Data?.Count > 0
                                ? subscription.Items.Data[0].CurrentPeriodEnd
                                : DateTime.UtcNow.AddMonths(1);

                            user.SetStripeCustomerId(session.CustomerId ?? session.Customer?.Id ?? "");
                            user.SetStripeSubscription(subscriptionId, periodEnd, GetSubscriptionInterval(subscription));

                            // Clear referral coupon after successful checkout (mark as redeemed)
                            if (!string.IsNullOrEmpty(user.ReferralCouponId))
                            {
                                logger.LogInformation("Clearing referral coupon {CouponId} for user {UserId} after checkout", user.ReferralCouponId, uid);
                                user.SetReferralCoupon(null);
                            }

                            await unitOfWork.SaveChangesAsync(ct);
                            logger.LogInformation("User {UserId} upgraded to Pro, expires {Expires}", uid, periodEnd);
                        }
                    }
                    else
                    {
                        logger.LogWarning("Could not extract userId from checkout session metadata");
                    }
                    break;
                }

                case "invoice.paid":
                {
                    var invoice = stripeEvent.Data.Object as Invoice;
                    // v50: SubscriptionId is under Parent.SubscriptionDetails
                    var invoiceSubId = invoice?.Parent?.SubscriptionDetails?.SubscriptionId;
                    logger.LogInformation("Invoice paid: subId={SubId}", invoiceSubId);

                    if (invoiceSubId is not null)
                    {
                        var user = await userRepository.FindOneTrackedAsync(
                            u => u.StripeSubscriptionId == invoiceSubId, cancellationToken: ct);
                        if (user is not null)
                        {
                            var subService = new SubscriptionService();
                            var subscription = await subService.GetAsync(invoiceSubId, cancellationToken: ct);
                            var periodEnd = subscription.Items?.Data?.Count > 0
                                ? subscription.Items.Data[0].CurrentPeriodEnd
                                : DateTime.UtcNow.AddMonths(1);
                            user.SetStripeSubscription(invoiceSubId, periodEnd, GetSubscriptionInterval(subscription));
                            await unitOfWork.SaveChangesAsync(ct);
                            logger.LogInformation("User {UserId} subscription renewed, expires {Expires}", user.Id, periodEnd);
                        }
                    }
                    break;
                }

                case "customer.subscription.deleted":
                {
                    var subscription = stripeEvent.Data.Object as Subscription;
                    logger.LogInformation("Subscription deleted: {SubId}", subscription?.Id);
                    if (subscription is not null)
                    {
                        var user = await userRepository.FindOneTrackedAsync(
                            u => u.StripeSubscriptionId == subscription.Id, cancellationToken: ct);
                        if (user is not null)
                        {
                            user.CancelSubscription();
                            await unitOfWork.SaveChangesAsync(ct);
                            logger.LogInformation("User {UserId} downgraded to Free", user.Id);
                        }
                    }
                    break;
                }

                case "customer.subscription.updated":
                {
                    var subscription = stripeEvent.Data.Object as Subscription;
                    logger.LogInformation("Subscription updated: {SubId}, status={Status}", subscription?.Id, subscription?.Status);
                    if (subscription is not null)
                    {
                        var user = await userRepository.FindOneTrackedAsync(
                            u => u.StripeSubscriptionId == subscription.Id, cancellationToken: ct);
                        if (user is not null)
                        {
                            if (subscription.Status == "active")
                            {
                                var periodEnd = subscription.Items?.Data?.Count > 0
                                    ? subscription.Items.Data[0].CurrentPeriodEnd
                                    : DateTime.UtcNow.AddMonths(1);
                                user.SetStripeSubscription(subscription.Id, periodEnd, GetSubscriptionInterval(subscription));
                            }
                            else if (subscription.Status == "canceled" || subscription.Status == "unpaid")
                            {
                                user.CancelSubscription();
                            }
                            await unitOfWork.SaveChangesAsync(ct);
                        }
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Stripe webhook event {Type}", stripeEvent.Type);
            // Still return 200 to prevent Stripe from retrying
        }

        return Ok();
    }

    private static SubscriptionInterval GetSubscriptionInterval(Stripe.Subscription subscription)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();
        var interval = item?.Price?.Recurring?.Interval;
        var count = item?.Price?.Recurring?.IntervalCount ?? 1;

        return (interval, count) switch
        {
            ("year", _) => SubscriptionInterval.Yearly,
            _ => SubscriptionInterval.Monthly
        };
    }
}
