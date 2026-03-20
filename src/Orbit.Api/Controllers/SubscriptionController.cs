using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
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
        bool IsLifetimePro);

    [HttpPost("checkout")]
    public async Task<IActionResult> CreateCheckout(
        [FromBody] CreateCheckoutRequest request,
        CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null) return NotFound(new { error = "User not found" });

        // Prefer X-Forwarded-For (set by BFF/reverse proxy) over direct connection IP
        var forwardedFor = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var ip = forwardedFor?.Split(',')[0].Trim()
                 ?? HttpContext.Connection.RemoteIpAddress?.ToString();
        var countryCode = await geoLocationService.GetCountryCodeAsync(ip, ct);
        var isBrazil = countryCode == "BR";

        var priceId = (request.Interval?.ToLower(), isBrazil) switch
        {
            ("yearly", true) => _settings.YearlyPriceIdBrl,
            ("yearly", false) => _settings.YearlyPriceIdUsd,
            ("semiannual", true) => _settings.SemiAnnualPriceIdBrl,
            ("semiannual", false) => _settings.SemiAnnualPriceIdUsd,
            ("monthly", true) => _settings.MonthlyPriceIdBrl,
            (_, false) => _settings.MonthlyPriceIdUsd,
            _ => _settings.MonthlyPriceIdBrl
        };

        StripeConfiguration.ApiKey = _settings.SecretKey;

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

        var sessionService = new SessionService();
        var session = await sessionService.CreateAsync(new SessionCreateOptions
        {
            Customer = user.StripeCustomerId,
            Mode = "subscription",
            LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            SuccessUrl = _settings.SuccessUrl,
            CancelUrl = _settings.CancelUrl,
            Metadata = new Dictionary<string, string> { { "userId", userId.ToString() } }
        }, cancellationToken: ct);

        logger.LogInformation("Checkout created for user {UserId} price={PriceId} country={Country}", userId, priceId, countryCode);
        return Ok(new CheckoutResponse(session.Url));
    }

    [HttpPost("portal")]
    public async Task<IActionResult> CreatePortal(CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null) return NotFound(new { error = "User not found" });

        if (string.IsNullOrEmpty(user.StripeCustomerId))
            return BadRequest(new { error = "No subscription found" });

        StripeConfiguration.ApiKey = _settings.SecretKey;

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
        if (user is null) return NotFound(new { error = "User not found" });

        return Ok(new SubscriptionStatusResponse(
            user.HasProAccess ? "pro" : "free",
            user.HasProAccess,
            user.IsTrialActive,
            user.TrialEndsAt,
            user.PlanExpiresAt,
            user.AiMessagesUsedThisMonth,
            await payGate.GetAiMessageLimit(user.Id, ct),
            user.IsLifetimePro));
    }

    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook(CancellationToken ct)
    {
        StripeConfiguration.ApiKey = _settings.SecretKey;

        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(ct);
        logger.LogInformation("Stripe webhook received, body length: {Length}", json.Length);

        Event stripeEvent;
        try
        {
            if (!string.IsNullOrEmpty(_settings.WebhookSecret))
            {
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _settings.WebhookSecret,
                    throwOnApiVersionMismatch: false);
            }
            else
            {
                stripeEvent = EventUtility.ParseEvent(json);
            }
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
                            user.SetStripeSubscription(subscriptionId, periodEnd);
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
                            user.SetStripeSubscription(invoiceSubId, periodEnd);
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
                                user.SetStripeSubscription(subscription.Id, periodEnd);
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
}
