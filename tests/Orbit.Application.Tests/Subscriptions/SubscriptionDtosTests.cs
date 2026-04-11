using FluentAssertions;
using Orbit.Application.Subscriptions;

namespace Orbit.Application.Tests.Subscriptions;

public class SubscriptionDtosTests
{
    [Fact]
    public void CheckoutResponse_SetsUrl()
    {
        var response = new CheckoutResponse("https://checkout.stripe.com/session123");
        response.Url.Should().Be("https://checkout.stripe.com/session123");
    }

    [Fact]
    public void PortalResponse_SetsUrl()
    {
        var response = new PortalResponse("https://billing.stripe.com/session456");
        response.Url.Should().Be("https://billing.stripe.com/session456");
    }

    [Fact]
    public void SubscriptionStatusResponse_SetsAllProperties()
    {
        var expiresAt = DateTime.UtcNow.AddDays(30);
        var trialEndsAt = DateTime.UtcNow.AddDays(7);
        var response = new SubscriptionStatusResponse(
            "Pro", true, true, trialEndsAt, expiresAt, 5, 500, false, "monthly");

        response.Plan.Should().Be("Pro");
        response.HasProAccess.Should().BeTrue();
        response.IsTrialActive.Should().BeTrue();
        response.TrialEndsAt.Should().Be(trialEndsAt);
        response.PlanExpiresAt.Should().Be(expiresAt);
        response.AiMessagesUsed.Should().Be(5);
        response.AiMessagesLimit.Should().Be(500);
        response.IsLifetimePro.Should().BeFalse();
        response.SubscriptionInterval.Should().Be("monthly");
    }

    [Fact]
    public void SubscriptionStatusResponse_NullOptionalFields()
    {
        var response = new SubscriptionStatusResponse(
            "Free", false, false, null, null, 0, 20, false, null);

        response.TrialEndsAt.Should().BeNull();
        response.PlanExpiresAt.Should().BeNull();
        response.SubscriptionInterval.Should().BeNull();
    }

    [Fact]
    public void AdRewardResponse_SetsAllProperties()
    {
        var response = new AdRewardResponse(5, 10, 25);
        response.BonusMessagesGranted.Should().Be(5);
        response.TotalBonusMessages.Should().Be(10);
        response.NewLimit.Should().Be(25);
    }

    [Fact]
    public void PlanPriceDto_SetsProperties()
    {
        var dto = new PlanPriceDto(999, "usd");
        dto.UnitAmount.Should().Be(999);
        dto.Currency.Should().Be("usd");
    }

    [Fact]
    public void PlansResponse_SetsAllProperties()
    {
        var monthly = new PlanPriceDto(999, "usd");
        var yearly = new PlanPriceDto(7999, "usd");
        var response = new PlansResponse(monthly, yearly, 33, 20, "usd");

        response.Monthly.UnitAmount.Should().Be(999);
        response.Yearly.UnitAmount.Should().Be(7999);
        response.SavingsPercent.Should().Be(33);
        response.CouponPercentOff.Should().Be(20);
        response.Currency.Should().Be("usd");
    }

    [Fact]
    public void PaymentMethodDto_SetsProperties()
    {
        var dto = new PaymentMethodDto("visa", "4242", 12, 2027);
        dto.Brand.Should().Be("visa");
        dto.Last4.Should().Be("4242");
        dto.ExpMonth.Should().Be(12);
        dto.ExpYear.Should().Be(2027);
    }

    [Fact]
    public void InvoiceDto_SetsAllProperties()
    {
        var date = DateTime.UtcNow;
        var dto = new InvoiceDto("inv_123", date, 999, "usd", "paid",
            "https://stripe.com/invoice/123", "https://stripe.com/invoice/123.pdf", "subscription_create");

        dto.Id.Should().Be("inv_123");
        dto.Date.Should().Be(date);
        dto.AmountPaid.Should().Be(999);
        dto.Currency.Should().Be("usd");
        dto.Status.Should().Be("paid");
        dto.HostedInvoiceUrl.Should().Contain("stripe.com");
        dto.InvoicePdf.Should().Contain(".pdf");
        dto.BillingReason.Should().Be("subscription_create");
    }

    [Fact]
    public void BillingDetailsResponse_SetsAllProperties()
    {
        var paymentMethod = new PaymentMethodDto("mastercard", "5555", 6, 2026);
        var invoices = new List<InvoiceDto>
        {
            new("inv_1", DateTime.UtcNow, 999, "usd", "paid", null, null, "subscription_cycle")
        };
        var response = new BillingDetailsResponse(
            "active", DateTime.UtcNow.AddDays(30), false,
            "monthly", 999, "usd", paymentMethod, invoices);

        response.Status.Should().Be("active");
        response.CancelAtPeriodEnd.Should().BeFalse();
        response.Interval.Should().Be("monthly");
        response.AmountPerPeriod.Should().Be(999);
        response.Currency.Should().Be("usd");
        response.PaymentMethod.Should().NotBeNull();
        response.PaymentMethod!.Brand.Should().Be("mastercard");
        response.RecentInvoices.Should().HaveCount(1);
    }

    [Fact]
    public void BillingDetailsResponse_NullPaymentMethod()
    {
        var response = new BillingDetailsResponse(
            "active", DateTime.UtcNow, false,
            "yearly", 7999, "usd", null, new List<InvoiceDto>());

        response.PaymentMethod.Should().BeNull();
        response.RecentInvoices.Should().BeEmpty();
    }
}
