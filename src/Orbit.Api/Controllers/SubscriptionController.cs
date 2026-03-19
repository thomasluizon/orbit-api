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
    IOptions<StripeSettings> stripeSettings,
    IGeoLocationService geoLocationService) : ControllerBase
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
            user.HasProAccess ? 500 : 50,
            user.IsLifetimePro));
    }

    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook(CancellationToken ct)
    {
        StripeConfiguration.ApiKey = _settings.SecretKey;

        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(ct);

        Event stripeEvent;
        try
        {
            if (!string.IsNullOrEmpty(_settings.WebhookSecret))
            {
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _settings.WebhookSecret);
            }
            else
            {
                stripeEvent = EventUtility.ParseEvent(json);
            }
        }
        catch (StripeException)
        {
            return BadRequest();
        }

        switch (stripeEvent.Type)
        {
            case Stripe.EventTypes.CheckoutSessionCompleted:
            {
                var session = stripeEvent.Data.Object as Session;
                if (session?.Metadata.TryGetValue("userId", out var userIdStr) == true
                    && Guid.TryParse(userIdStr, out var uid))
                {
                    var user = await userRepository.FindOneTrackedAsync(u => u.Id == uid, cancellationToken: ct);
                    if (user is not null && session.SubscriptionId is not null)
                    {
                        var subService = new SubscriptionService();
                        var subscription = await subService.GetAsync(session.SubscriptionId, cancellationToken: ct);
                        user.SetStripeSubscription(session.SubscriptionId, subscription.Items.Data[0].CurrentPeriodEnd);
                        await unitOfWork.SaveChangesAsync(ct);
                    }
                }
                break;
            }

            case Stripe.EventTypes.InvoicePaid:
            {
                var invoice = stripeEvent.Data.Object as Invoice;
                var invoiceSubId = invoice?.Parent?.SubscriptionDetails?.SubscriptionId;
                if (invoiceSubId is not null)
                {
                    var user = await userRepository.FindOneTrackedAsync(
                        u => u.StripeSubscriptionId == invoiceSubId, cancellationToken: ct);
                    if (user is not null)
                    {
                        var subService = new SubscriptionService();
                        var subscription = await subService.GetAsync(invoiceSubId, cancellationToken: ct);
                        user.SetStripeSubscription(invoiceSubId, subscription.Items.Data[0].CurrentPeriodEnd);
                        await unitOfWork.SaveChangesAsync(ct);
                    }
                }
                break;
            }

            case Stripe.EventTypes.CustomerSubscriptionDeleted:
            {
                var subscription = stripeEvent.Data.Object as Subscription;
                if (subscription is not null)
                {
                    var user = await userRepository.FindOneTrackedAsync(
                        u => u.StripeSubscriptionId == subscription.Id, cancellationToken: ct);
                    if (user is not null)
                    {
                        user.CancelSubscription();
                        await unitOfWork.SaveChangesAsync(ct);
                    }
                }
                break;
            }

            case Stripe.EventTypes.CustomerSubscriptionUpdated:
            {
                var subscription = stripeEvent.Data.Object as Subscription;
                if (subscription is not null)
                {
                    var user = await userRepository.FindOneTrackedAsync(
                        u => u.StripeSubscriptionId == subscription.Id, cancellationToken: ct);
                    if (user is not null)
                    {
                        if (subscription.Status == "active")
                            user.SetStripeSubscription(subscription.Id, subscription.Items.Data[0].CurrentPeriodEnd);
                        else if (subscription.Status == "canceled" || subscription.Status == "unpaid")
                            user.CancelSubscription();
                        await unitOfWork.SaveChangesAsync(ct);
                    }
                }
                break;
            }
        }

        return Ok();
    }
}
