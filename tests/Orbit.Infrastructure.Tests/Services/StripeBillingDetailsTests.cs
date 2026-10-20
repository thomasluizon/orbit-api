using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<StripeBillingService> _logger = Substitute.For<ILogger<StripeBillingService>>();
    private readonly StripeBillingService _billing;
    private readonly GetBillingDetailsQueryHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Subscription _subscription;

    public StripeBillingDetailsTests()
    {
        _logger.IsEnabled(LogLevel.Warning).Returns(true);
        _logger.ClearReceivedCalls();
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

        _billing = new StripeBillingService(
            new StripeServiceClients(new CustomerService(), new Stripe.Checkout.SessionService(),
                new Stripe.BillingPortal.SessionService(), _subscriptions, _invoices, new PriceService(), new CouponService()),
            _logger);
        var user = User.Create("Subscriber", "subscriber@example.com").Value;
        user.SetStripeCustomerId("cus_referral");
        user.SetStripeSubscription("sub_referral", periodEnd);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(user);
        _handler = new GetBillingDetailsQueryHandler(users, _billing, NullLogger<GetBillingDetailsQueryHandler>.Instance);
    }

    private static InvoiceLineItem RecurringLine(long subtotal = 1000) => new()
    {
        Subtotal = subtotal,
        Currency = "brl",
        Parent = new InvoiceLineItemParent
        {
            Type = "subscription_item_details",
            SubscriptionItemDetails = new InvoiceLineItemParentSubscriptionItemDetails
            {
                Subscription = "sub_referral",
                Proration = false
            }
        }
    };

    private void Preview(params InvoiceLineItem[] lines) =>
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new Invoice
            {
                AmountDue = 0,
                StartingBalance = -1000,
                Currency = "brl",
                Lines = new StripeList<InvoiceLineItem> { Data = [.. lines] }
            });

    [Fact]
    public async Task Handle_ReferralDiscountChangesRecurringAmountWithoutChangingPlanPrice()
    {
        var line = RecurringLine();
        Preview(line);
        using var cts = new CancellationTokenSource();

        var undiscounted = await _handler.Handle(new GetBillingDetailsQuery(_userId), cts.Token);

        var referral = new Discount
        {
            Id = "di_referral",
            Source = new DiscountSource
            {
                Coupon = new Coupon { Id = "coupon_referral", PercentOff = 20, Duration = "forever" }
            }
        };
        _subscription.Discounts = [referral];
        line.DiscountAmounts = [new InvoiceLineItemDiscountAmount { Amount = 200, Discount = referral }];

        var discounted = await _handler.Handle(new GetBillingDetailsQuery(_userId), cts.Token);

        undiscounted.IsSuccess.Should().BeTrue();
        undiscounted.Value.BilledAmountPerPeriod.Should().Be(1000);
        discounted.IsSuccess.Should().BeTrue();
        discounted.Value.BilledAmountPerPeriod.Should().Be(800);
        discounted.Value.BilledAmountPerPeriod.Should().BeLessThan(undiscounted.Value.BilledAmountPerPeriod!.Value);
        discounted.Value.AmountPerPeriod.Should().Be(1000);
        discounted.Value.Currency.Should().Be("brl");
        await _invoices.Received(2).CreatePreviewAsync(
            Arg.Is<InvoiceCreatePreviewOptions>(options => options.Subscription == "sub_referral"
                && options.PreviewMode == "recurring" && options.Discounts == null && options.SubscriptionDetails == null),
            Arg.Any<RequestOptions>(), cts.Token);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1000, 0)]
    public async Task Handle_UsesStripeCalculatedDiscountAmounts(long discountAmount, long expected)
    {
        var line = RecurringLine();
        line.DiscountAmounts = [new InvoiceLineItemDiscountAmount { Amount = discountAmount }];
        Preview(line);

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AmountPerPeriod.Should().Be(1000);
        result.Value.BilledAmountPerPeriod.Should().Be(expected);
    }

    [Fact]
    public async Task Handle_RecurringAmountExcludesProrationsOneTimeItemsAndOtherSubscriptions()
    {
        var proration = RecurringLine(400);
        proration.Parent.SubscriptionItemDetails.Proration = true;
        var unrelated = RecurringLine(700);
        unrelated.Parent.SubscriptionItemDetails.Subscription = "sub_other";
        Preview(RecurringLine(), proration, unrelated, new InvoiceLineItem { Subtotal = 600, Currency = "brl" });

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BilledAmountPerPeriod.Should().Be(1000);
    }

    [Fact]
    public async Task Handle_RecurringAmountSumsSubscriptionLinesAndStackedStripeDiscounts()
    {
        var first = RecurringLine();
        first.DiscountAmounts = [new InvoiceLineItemDiscountAmount { Amount = 200 }, new InvoiceLineItemDiscountAmount { Amount = 100 }];
        Preview(first, RecurringLine(300));

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BilledAmountPerPeriod.Should().Be(1000);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("paginated")]
    [InlineData("currency")]
    public async Task Handle_UnusablePreview_ReturnsNullInsteadOfAnIncompleteAmount(string scenario)
    {
        var invoice = new Invoice
        {
            Currency = scenario == "currency" ? "usd" : "brl",
            Lines = scenario == "missing" ? null : new StripeList<InvoiceLineItem>
            {
                HasMore = scenario == "paginated",
                Data = scenario == "empty" ? [] : [RecurringLine()]
            }
        };
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(invoice);

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BilledAmountPerPeriod.Should().BeNull();
    }

    [Fact]
    public async Task Handle_NoUpcomingInvoice_PreservesSubscriptionWithNullBilledAmount()
    {
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException("No upcoming invoice")
            {
                StripeError = new StripeError { Code = "invoice_upcoming_none" }
            });

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AmountPerPeriod.Should().Be(1000);
        result.Value.BilledAmountPerPeriod.Should().BeNull();
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("canceled", false)]
    [InlineData("active", true)]
    [InlineData("trialing", false)]
    [InlineData("paused", false)]
    public async Task Handle_SubscriptionWithoutRecurringPreview_ReturnsNullWithoutCallingPreview(string status, bool cancelAtPeriodEnd)
    {
        _subscription.Status = status;
        _subscription.CancelAtPeriodEnd = cancelAtPeriodEnd;

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BilledAmountPerPeriod.Should().BeNull();
        result.Value.Status.Should().Be(status);
        result.Value.CancelAtPeriodEnd.Should().Be(cancelAtPeriodEnd);
        await _invoices.DidNotReceive().CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>());
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task GetSubscriptionDetails_DoesNotRequestInvoicePreview()
    {
        Preview(RecurringLine());

        await _billing.GetSubscriptionDetailsAsync("sub_referral", CancellationToken.None);

        await _invoices.DidNotReceive().CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]
    [InlineData(HttpStatusCode.TooManyRequests, null)]
    [InlineData(HttpStatusCode.Unauthorized, null)]
    [InlineData(HttpStatusCode.NotFound, "resource_missing")]
    public async Task Handle_InvoicePreviewFailure_PreservesBillingDetailsWithNullAmount(HttpStatusCode status, string? code)
    {
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException(status, new StripeError { Code = code }, "Preview unavailable"));

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BilledAmountPerPeriod.Should().BeNull();
        result.Value.AmountPerPeriod.Should().Be(1000);
        result.Value.Status.Should().Be("active");
        result.Value.RecentInvoices.Should().BeEmpty();
        _logger.ReceivedCalls().Should().ContainSingle(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_TransportFailure_PreservesBillingDetailsWithNullAmount(bool timeout)
    {
        var failure = timeout ? (Exception)new TimeoutException("Preview timeout") : new HttpRequestException("Preview offline");
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Invoice>(failure));

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BilledAmountPerPeriod.Should().BeNull();
    }

    [Fact]
    public async Task Handle_InvoicePreviewTransientFailure_Retries()
    {
        _invoices.CreatePreviewAsync(Arg.Any<InvoiceCreatePreviewOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new StripeException(HttpStatusCode.ServiceUnavailable, new StripeError(), "Preview unavailable"),
                _ => new Invoice { Currency = "brl", Lines = new StripeList<InvoiceLineItem> { Data = [RecurringLine()] } });

        var result = await _handler.Handle(new GetBillingDetailsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BilledAmountPerPeriod.Should().Be(1000);
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
