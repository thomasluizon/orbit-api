using FluentAssertions;
using Orbit.Application.Common;
using NSubstitute.ExceptionExtensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Infrastructure.Services;
using Stripe;
using Stripe.Checkout;
using PortalCreateOptions = Stripe.BillingPortal.SessionCreateOptions;
using PortalSession = Stripe.BillingPortal.Session;
using PortalSessionService = Stripe.BillingPortal.SessionService;

namespace Orbit.Infrastructure.Tests.Services;

public class StripeBillingServiceTests
{
    private const string SuccessUrl = "https://app.useorbit.org/profile?subscription=success";
    private const string CancelUrl = "https://app.useorbit.org/upgrade";
    private const string ReturnUrl = "https://app.useorbit.org/profile";
    private static readonly Guid UserId = Guid.Parse("7092bf88-d132-46df-b4ee-2dcc2197cd69");
    private readonly List<(SessionCreateOptions Options, RequestOptions Request)> _checkoutRequests = [];
    private readonly List<(PortalCreateOptions Options, RequestOptions Request)> _portalRequests = [];
    private readonly SubscriptionService _subscriptions = Substitute.For<SubscriptionService>();
    private readonly StripeBillingService _service;

    public StripeBillingServiceTests()
    {
        var checkout = Substitute.For<SessionService>();
        checkout.CreateAsync(Arg.Any<SessionCreateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _checkoutRequests.Add((call.ArgAt<SessionCreateOptions>(0), call.ArgAt<RequestOptions>(1)));
                return Task.FromResult(new Session { Url = "https://checkout.stripe.com/session" });
            });
        var portal = Substitute.For<PortalSessionService>();
        portal.CreateAsync(Arg.Any<PortalCreateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _portalRequests.Add((call.ArgAt<PortalCreateOptions>(0), call.ArgAt<RequestOptions>(1)));
                return Task.FromResult(new PortalSession { Url = "https://billing.stripe.com/session" });
            });
        _service = new StripeBillingService(
            new StripeServiceClients(new CustomerService(), checkout, portal, _subscriptions,
                new InvoiceService(), new PriceService(), new CouponService()),
            NullLogger<StripeBillingService>.Instance);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound, null)]
    [InlineData(System.Net.HttpStatusCode.BadRequest, "resource_missing")]
    public async Task GetSubscriptionDetailsAsync_MissingSubscription_ReturnsNull(System.Net.HttpStatusCode status, string? code)
    {
        _subscriptions.GetAsync("sub_paid", Arg.Any<SubscriptionGetOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException(status, new StripeError { Code = code }, "missing"));

        var details = await _service.GetSubscriptionDetailsAsync("sub_paid", CancellationToken.None);

        details.Should().BeNull();
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound, null)]
    [InlineData(System.Net.HttpStatusCode.BadRequest, "resource_missing")]
    public async Task SetSubscriptionCancelAtPeriodEndAsync_DisappearedAfterRead_Completes(System.Net.HttpStatusCode status, string? code)
    {
        _subscriptions.UpdateAsync("sub_paid", Arg.Any<SubscriptionUpdateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException(status, new StripeError { Code = code }, "missing"));

        var run = () => _service.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, CancellationToken.None);

        await run.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
    public async Task GetSubscriptionDetailsAsync_ProviderOutage_RemainsRetryable(System.Net.HttpStatusCode status)
    {
        _subscriptions.GetAsync("sub_paid", Arg.Any<SubscriptionGetOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException(status, new StripeError(), "unavailable"));

        var run = () => _service.GetSubscriptionDetailsAsync("sub_paid", CancellationToken.None);

        await run.Should().ThrowAsync<BillingProviderException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionProviderTimeout_RemainsRetryable(bool update)
    {
        var failure = new TaskCanceledException("provider timeout");
        _subscriptions.GetAsync("sub_paid", Arg.Any<SubscriptionGetOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);
        _subscriptions.UpdateAsync("sub_paid", Arg.Any<SubscriptionUpdateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        Func<Task> run = () => update
            ? _service.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, CancellationToken.None)
            : _service.GetSubscriptionDetailsAsync("sub_paid", CancellationToken.None);

        (await run.Should().ThrowAsync<BillingProviderException>()).Which.InnerException.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task GetSubscriptionDetailsAsync_CallerCancellation_IsNotWrapped()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _subscriptions.GetAsync("sub_paid", Arg.Any<SubscriptionGetOptions>(), Arg.Any<RequestOptions>(), cts.Token)
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var run = () => _service.GetSubscriptionDetailsAsync("sub_paid", cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetSubscriptionCancelAtPeriodEndAsync_UpdatesRenewalWithoutImmediateCancellation(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        _subscriptions.UpdateAsync("sub_paid", Arg.Any<SubscriptionUpdateOptions>(), Arg.Any<RequestOptions>(), cts.Token)
            .Returns(new Subscription());

        await _service.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", cancel, cts.Token);

        await _subscriptions.Received(1).UpdateAsync("sub_paid",
            Arg.Is<SubscriptionUpdateOptions>(options => options.CancelAtPeriodEnd == cancel),
            Arg.Any<RequestOptions>(), cts.Token);
        await _subscriptions.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<SubscriptionCancelOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetSubscriptionCancelAtPeriodEndAsync_StripeFailure_WrapsProviderError()
    {
        var failure = new StripeException("rejected");
        _subscriptions.UpdateAsync("sub_paid", Arg.Any<SubscriptionUpdateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        var run = () => _service.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, CancellationToken.None);

        (await run.Should().ThrowAsync<BillingProviderException>()).Which.InnerException.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task SetSubscriptionCancelAtPeriodEndAsync_TransientFailure_Retries()
    {
        _subscriptions.UpdateAsync("sub_paid", Arg.Any<SubscriptionUpdateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new StripeException("unavailable") { HttpStatusCode = System.Net.HttpStatusCode.ServiceUnavailable },
                _ => new Subscription());

        await _service.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, CancellationToken.None);

        await _subscriptions.Received(2).UpdateAsync("sub_paid", Arg.Any<SubscriptionUpdateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("coupon_referral")]
    public async Task Checkout_IdenticalRequests_UseSameKey(string? coupon)
    {
        var first = await CheckoutKeyAsync(coupon: coupon);
        var second = await CheckoutKeyAsync(coupon: coupon);

        first.Should().NotBeNullOrEmpty();
        second.Should().Be(first);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("customer")]
    [InlineData("price")]
    [InlineData("user")]
    [InlineData("coupon")]
    public async Task Checkout_ChangedRequestParameter_UsesDifferentKey(string parameter)
    {
        var first = await CheckoutKeyAsync();
        var second = await CheckoutKeyAsync(
            customer: parameter == "customer" ? "cus_other" : "cus_test",
            price: parameter == "price" ? "price_other" : "price_test",
            success: parameter == "success" ? "https://app.useorbit.org/upgrade?subscription=success" : SuccessUrl,
            cancel: parameter == "cancel" ? "https://app-staging.useorbit.org/upgrade" : CancelUrl,
            user: parameter == "user" ? Guid.Parse("497685db-9fc3-4d2a-ad73-f6bf5524d339") : UserId,
            coupon: parameter == "coupon" ? "coupon_referral" : null);

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Checkout_EmptyAndNullCoupon_UseSameKey()
    {
        var first = await CheckoutKeyAsync();
        var second = await CheckoutKeyAsync(coupon: "");

        second.Should().Be(first);
    }

    [Fact]
    public async Task Checkout_StdCouponAndNoCoupon_UseDifferentKeys()
    {
        var first = await CheckoutKeyAsync();
        var second = await CheckoutKeyAsync(coupon: "std");

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Checkout_LongRequestParameters_KeyFitsStripeLimit()
    {
        var key = await CheckoutKeyAsync(customer: new string('c', 256), price: new string('p', 256),
            coupon: new string('d', 256), success: SuccessUrl + new string('s', 1024),
            cancel: CancelUrl + new string('q', 1024));

        key.Length.Should().BeLessThanOrEqualTo(255);
    }

    [Theory]
    [InlineData("https://app.useorbit.org/profile?subscription=success", "https://app.useorbit.org/upgrade")]
    [InlineData("https://app-staging.useorbit.org/upgrade?subscription=success", "https://app-staging.useorbit.org/upgrade")]
    public async Task Checkout_ConfiguredReturnUrls_AreSentUnchanged(string success, string cancel)
    {
        await CheckoutKeyAsync(success: success, cancel: cancel);

        var options = _checkoutRequests.Single().Options;
        options.SuccessUrl.Should().Be(success);
        options.CancelUrl.Should().Be(cancel);
    }

    [Theory]
    [InlineData("7092bf88-d132-46df-b4ee-2dcc2197cd69", null)]
    [InlineData("497685db-9fc3-4d2a-ad73-f6bf5524d339", "coupon_referral")]
    public async Task Checkout_UserId_IsSentInSessionMetadata(string user, string? coupon)
    {
        var userId = Guid.Parse(user);
        await CheckoutKeyAsync(user: userId, coupon: coupon);

        var metadata = _checkoutRequests.Single().Options.Metadata;
        metadata.Should().ContainKey("userId");
        metadata["userId"].Should().Be(userId.ToString());
    }

    [Fact]
    public async Task Portal_IdenticalRequests_UseSameKey()
    {
        var first = await PortalKeyAsync();
        var second = await PortalKeyAsync();

        first.Should().NotBeNullOrEmpty();
        second.Should().Be(first);
    }

    [Theory]
    [InlineData("return")]
    [InlineData("customer")]
    public async Task Portal_ChangedRequestParameter_UsesDifferentKey(string parameter)
    {
        var first = await PortalKeyAsync();
        var second = await PortalKeyAsync(
            customer: parameter == "customer" ? "cus_other" : "cus_test",
            returnUrl: parameter == "return" ? "https://app.useorbit.org/upgrade" : ReturnUrl);

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Portal_LongRequestParameters_KeyFitsStripeLimit()
    {
        var key = await PortalKeyAsync(customer: new string('c', 256), returnUrl: ReturnUrl + new string('r', 1024));

        key.Length.Should().BeLessThanOrEqualTo(255);
    }

    [Theory]
    [InlineData("https://app.useorbit.org/profile")]
    [InlineData("https://app-staging.useorbit.org/upgrade")]
    public async Task Portal_ConfiguredReturnUrl_IsSentUnchanged(string returnUrl)
    {
        await PortalKeyAsync(returnUrl: returnUrl);

        _portalRequests.Single().Options.ReturnUrl.Should().Be(returnUrl);
    }

    private async Task<string> CheckoutKeyAsync(string customer = "cus_test", string price = "price_test",
        string success = SuccessUrl, string cancel = CancelUrl, Guid? user = null, string? coupon = null)
    {
        await _service.CreateCheckoutSessionAsync(customer, price, success, cancel, user ?? UserId,
            coupon, CancellationToken.None);
        return _checkoutRequests[^1].Request.IdempotencyKey;
    }

    private async Task<string> PortalKeyAsync(string customer = "cus_test", string returnUrl = ReturnUrl)
    {
        await _service.CreatePortalSessionAsync(customer, returnUrl, CancellationToken.None);
        return _portalRequests[^1].Request.IdempotencyKey;
    }
}
