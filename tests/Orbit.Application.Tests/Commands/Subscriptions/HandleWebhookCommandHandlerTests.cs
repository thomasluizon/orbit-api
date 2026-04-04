using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;
using Stripe.Checkout;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class HandleWebhookCommandHandlerTests
{
    private const string WebhookSecret = "whsec_test_secret";

    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SubscriptionService _subscriptionService = Substitute.For<SubscriptionService>();
    private readonly HandleWebhookCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public HandleWebhookCommandHandlerTests()
    {
        // Stripe.net v50+ EventConverter requires StripeConfiguration to be initialized
        StripeConfiguration.ApiKey ??= "sk_test_fake_key_for_unit_tests";

        var settings = Options.Create(new StripeSettings
        {
            WebhookSecret = WebhookSecret
        });

        _handler = new HandleWebhookCommandHandler(
            _userRepo, _unitOfWork, settings, _subscriptionService,
            Substitute.For<ILogger<HandleWebhookCommandHandler>>());
    }

    [Fact]
    public async Task Handle_WebhookSecretNotConfigured_ReturnsFailure()
    {
        var settings = Options.Create(new StripeSettings { WebhookSecret = "" });
        var handler = new HandleWebhookCommandHandler(
            _userRepo, _unitOfWork, settings, _subscriptionService,
            Substitute.For<ILogger<HandleWebhookCommandHandler>>());

        var command = new HandleWebhookCommand("{}", "sig_test");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not configured");
    }

    [Fact]
    public async Task Handle_NullWebhookSecret_ReturnsFailure()
    {
        var settings = Options.Create(new StripeSettings { WebhookSecret = null! });
        var handler = new HandleWebhookCommandHandler(
            _userRepo, _unitOfWork, settings, _subscriptionService,
            Substitute.For<ILogger<HandleWebhookCommandHandler>>());

        var command = new HandleWebhookCommand("{}", "sig_test");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not configured");
    }

    [Fact]
    public async Task Handle_InvalidSignature_ReturnsFailure()
    {
        var command = new HandleWebhookCommand("{}", "invalid_signature");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("signature");
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_WithValidUser_UpgradesPro()
    {
        var subscriptionId = "sub_test_123";
        var customerId = "cus_test_456";
        var user = User.Create("Thomas", "test@example.com").Value;

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var subscription = CreateMockSubscription(subscriptionId);
        _subscriptionService.GetAsync(subscriptionId, Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(subscription);

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), subscriptionId, customerId));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_NoUserIdInMetadata_DoesNotThrow()
    {
        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            null, "sub_test", "cus_test"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_UserNotFound_DoesNotThrow()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_test", "cus_test"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_ClearsReferralCoupon()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetReferralCoupon("coupon_test");

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var subscription = CreateMockSubscription("sub_test");
        _subscriptionService.GetAsync("sub_test", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(subscription);

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_test", "cus_test"));

        var command = new HandleWebhookCommand(json, signature);
        await _handler.Handle(command, CancellationToken.None);

        user.ReferralCouponId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_SubscriptionDeleted_CancelsSubscription()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddMonths(1));

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var (json, signature) = BuildSignedEvent("customer.subscription.deleted",
            BuildSubscriptionJson("sub_test", "canceled"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.StripeSubscriptionId.Should().BeNull();
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SubscriptionDeleted_UserNotFound_DoesNotThrow()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var (json, signature) = BuildSignedEvent("customer.subscription.deleted",
            BuildSubscriptionJson("sub_test", "canceled"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SubscriptionUpdated_Active_UpdatesExpiry()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddMonths(1));

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var (json, signature) = BuildSignedEvent("customer.subscription.updated",
            BuildSubscriptionJson("sub_test", "active"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SubscriptionUpdated_Canceled_CancelsSubscription()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddMonths(1));

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var (json, signature) = BuildSignedEvent("customer.subscription.updated",
            BuildSubscriptionJson("sub_test", "canceled"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_SubscriptionUpdated_Unpaid_CancelsSubscription()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddMonths(1));

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var (json, signature) = BuildSignedEvent("customer.subscription.updated",
            BuildSubscriptionJson("sub_test", "unpaid"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_SubscriptionUpdated_UserNotFound_DoesNotThrow()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var (json, signature) = BuildSignedEvent("customer.subscription.updated",
            BuildSubscriptionJson("sub_test", "active"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvoicePaid_RenewsSubscription()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddMonths(1));

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var subscription = CreateMockSubscription("sub_test");
        _subscriptionService.GetAsync("sub_test", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(subscription);

        var (json, signature) = BuildSignedEvent("invoice.paid",
            BuildInvoiceJson("sub_test"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvoicePaid_NoSubscriptionId_DoesNotProcess()
    {
        var (json, signature) = BuildSignedEvent("invoice.paid",
            BuildInvoiceJson(null));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvoicePaid_UserNotFound_DoesNotProcess()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var (json, signature) = BuildSignedEvent("invoice.paid",
            BuildInvoiceJson("sub_test"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownEventType_ReturnsSuccess()
    {
        var (json, signature) = BuildSignedEvent("payment_intent.created",
            """{"id":"pi_test","object":"payment_intent"}""");

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // --- Helpers ---

    private static Subscription CreateMockSubscription(string subscriptionId)
    {
        return new Subscription
        {
            Id = subscriptionId,
            Status = "active",
            Items = new StripeList<SubscriptionItem>
            {
                Data =
                [
                    new SubscriptionItem
                    {
                        CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1),
                        Price = new Price
                        {
                            Recurring = new PriceRecurring
                            {
                                Interval = "month",
                                IntervalCount = 1
                            }
                        }
                    }
                ]
            }
        };
    }

    private static string BuildCheckoutSessionJson(string? userId, string subscriptionId, string customerId)
    {
        var metadata = userId is not null
            ? $$"""{"userId":"{{userId}}"}"""
            : "{}";

        return $$"""
        {
            "id": "cs_test_123",
            "object": "checkout.session",
            "subscription": "{{subscriptionId}}",
            "customer": "{{customerId}}",
            "metadata": {{metadata}}
        }
        """;
    }

    private static string BuildSubscriptionJson(string subscriptionId, string status)
    {
        return $$"""
        {
            "id": "{{subscriptionId}}",
            "object": "subscription",
            "status": "{{status}}",
            "items": {
                "object": "list",
                "data": [{
                    "id": "si_test",
                    "object": "subscription_item",
                    "current_period_end": {{DateTimeOffset.UtcNow.AddMonths(1).ToUnixTimeSeconds()}},
                    "price": {
                        "id": "price_test",
                        "object": "price",
                        "recurring": {
                            "interval": "month",
                            "interval_count": 1
                        }
                    }
                }]
            }
        }
        """;
    }

    private static string BuildInvoiceJson(string? subscriptionId)
    {
        var parentBlock = subscriptionId is not null
            ? $$"""
            "parent": {
                "type": "subscription_details",
                "subscription_details": {
                    "subscription": "{{subscriptionId}}"
                }
            }
            """
            : """
            "parent": null
            """;

        return $$"""
        {
            "id": "in_test_123",
            "object": "invoice",
            {{parentBlock}}
        }
        """;
    }

    private static (string Json, string Signature) BuildSignedEvent(string eventType, string dataObjectJson)
    {
        var eventJson = $$"""
        {
            "id": "evt_test_{{Guid.NewGuid():N}}",
            "object": "event",
            "api_version": "2026-02-25.clover",
            "type": "{{eventType}}",
            "created": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
            "livemode": false,
            "pending_webhooks": 0,
            "request": {
                "id": "req_test_123",
                "idempotency_key": null
            },
            "data": {
                "object": {{dataObjectJson}}
            }
        }
        """;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = $"{timestamp}.{eventJson}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        var signature = $"t={timestamp},v1={hex}";

        return (eventJson, signature);
    }
}
