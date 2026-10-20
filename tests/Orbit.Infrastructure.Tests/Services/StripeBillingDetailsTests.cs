using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;
using Stripe;

namespace Orbit.Infrastructure.Tests.Services;

public class StripeBillingDetailsTests
{
    private readonly SubscriptionService _subscriptions = Substitute.For<SubscriptionService>();
    private readonly InvoiceService _invoices = Substitute.For<InvoiceService>();
    private readonly GetBillingDetailsQueryHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Subscription _subscription;

    public StripeBillingDetailsTests()
    {
        var periodEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _subscription = new Subscription
        {
            Status = "active",
            Currency = "brl",
            Items = new StripeList<SubscriptionItem>
            {
                Data = [new SubscriptionItem
                {
                    CurrentPeriodEnd = periodEnd,
                    Price = new Price { UnitAmount = 1000, Recurring = new PriceRecurring { Interval = "month" } }
                }]
            }
        };
        _subscriptions.GetAsync("sub_referral", Arg.Any<SubscriptionGetOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_subscription);
        _invoices.ListAsync(Arg.Any<InvoiceListOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new StripeList<Invoice> { Data = [] });

        var billing = new StripeBillingService(
            new StripeServiceClients(new CustomerService(), new Stripe.Checkout.SessionService(),
                new Stripe.BillingPortal.SessionService(), _subscriptions, _invoices, new PriceService(), new CouponService()),
            NullLogger<StripeBillingService>.Instance);
        var user = User.Create("Subscriber", "subscriber@example.com").Value;
        user.SetStripeCustomerId("cus_referral");
        user.SetStripeSubscription("sub_referral", periodEnd);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(user);
        _handler = new GetBillingDetailsQueryHandler(users, billing, NullLogger<GetBillingDetailsQueryHandler>.Instance);
    }

    [Theory]
    [InlineData(true, 800)]
    [InlineData(false, 1000)]
    [InlineData(true, 0)]
    public async Task Handle_MapsStripeInvoiceAmountWithoutChangingPlanPrice(bool referral, long amountDue)
    {
        if (referral)
        {
            _subscription.Discounts = [new Discount
            {
                Source = new DiscountSource
                {
                    Coupon = new Coupon { Id = "coupon_referral", PercentOff = 20, Duration = "forever" }
                }
            }];
        }
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new Invoice { AmountDue = amountDue, Currency = "brl" });
        using var cts = new CancellationTokenSource();

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), cts.Token);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new
        {
            AmountPerPeriod = 1000L,
            BilledAmountPerPeriod = (long?)amountDue,
            Currency = "brl"
        });
        await _invoices.Received(1).CreatePreviewAsync(
            Arg.Is<InvoiceCreatePreviewOptions>(options => options.Subscription == "sub_referral"
                && options.Discounts == null && options.SubscriptionDetails == null && options.PreviewMode == null),
            Arg.Any<RequestOptions>(), cts.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_NoUpcomingInvoice_PreservesSubscriptionWithNullBilledAmount(bool cancelAtPeriodEnd)
    {
        _subscription.CancelAtPeriodEnd = cancelAtPeriodEnd;
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException(HttpStatusCode.BadRequest,
                new StripeError { Code = "invoice_upcoming_none" }, "No upcoming invoice"));

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new
        {
            AmountPerPeriod = 1000L,
            BilledAmountPerPeriod = (long?)null,
            CancelAtPeriodEnd = cancelAtPeriodEnd
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]
    [InlineData(HttpStatusCode.TooManyRequests, null)]
    [InlineData(HttpStatusCode.Unauthorized, null)]
    [InlineData(HttpStatusCode.NotFound, "resource_missing")]
    public async Task Handle_InvoicePreviewFailure_ReturnsBillingUnavailable(HttpStatusCode status, string? code)
    {
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException(status, new StripeError { Code = code }, "Preview unavailable"));

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BillingDetailsUnavailable);
    }

    [Fact]
    public async Task Handle_InvoicePreviewTransientFailure_Retries()
    {
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new StripeException(HttpStatusCode.ServiceUnavailable, new StripeError(), "Preview unavailable"),
                _ => new Invoice { AmountDue = 800, Currency = "brl" });

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _invoices.Received(2).CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvoicePreviewCallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), cts.Token)
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromCanceled<Invoice>(cts.Token);
            });

        var run = () => _handler.Handle(new GetBillingDetailsQuery(_userId), cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }
}
