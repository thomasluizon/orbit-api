using System.Data.Common;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Common;
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
    private readonly IGenericRepository<ProcessedStripeEvent> _processedRepo = Substitute.For<IGenericRepository<ProcessedStripeEvent>>();
    private readonly HandleWebhookCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public HandleWebhookCommandHandlerTests()
    {
        StripeConfiguration.ApiKey ??= "sk_test_fake_key_for_unit_tests";

        var settings = Options.Create(new StripeSettings
        {
            WebhookSecret = WebhookSecret
        });

        _handler = new HandleWebhookCommandHandler(
            _userRepo, _unitOfWork, settings, _subscriptionService, _processedRepo,
            Substitute.For<ILogger<HandleWebhookCommandHandler>>());
    }

    [Fact]
    public async Task Handle_WebhookSecretNotConfigured_ReturnsFailure()
    {
        var settings = Options.Create(new StripeSettings { WebhookSecret = "" });
        var handler = new HandleWebhookCommandHandler(
            _userRepo, _unitOfWork, settings, _subscriptionService, _processedRepo,
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
            _userRepo, _unitOfWork, settings, _subscriptionService, _processedRepo,
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

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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
    public async Task Handle_CheckoutSessionCompleted_NoUserIdInMetadata_ReturnsFailureSoStripeRetries()
    {
        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            null, "sub_test", "cus_test"));

        var command = new HandleWebhookCommand(json, signature);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_UserNotFound_DoesNotThrow()
    {
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
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

    [Fact]
    public async Task Handle_ReservesProcessedEventViaConstraint_WithoutCheckThenInsertPreCheck()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var subscription = CreateMockSubscription("sub_test");
        _subscriptionService.GetAsync("sub_test", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(subscription);

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_test", "cus_test"));

        var result = await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _processedRepo.DidNotReceive().AnyAsync(
            Arg.Any<Expression<Func<ProcessedStripeEvent, bool>>>(),
            Arg.Any<CancellationToken>());
        await _processedRepo.Received(1).AddAsync(
            Arg.Any<ProcessedStripeEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DuplicateEvent_UniqueViolationOnSave_IsIdempotentSuccess()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var subscription = CreateMockSubscription("sub_test");
        _subscriptionService.GetAsync("sub_test", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(subscription);

        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("duplicate", new FakeUniqueViolationException()));

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_test", "cus_test"));

        var result = await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Command_IsMarkedConcurrencyRetryable() =>
        typeof(HandleWebhookCommand).Should().BeAssignableTo<IConcurrencyRetryable>();

    [Fact]
    public async Task Handle_SaveThrowsConcurrencyConflict_PropagatesInsteadOfSwallowingAsFailure()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var subscription = CreateMockSubscription("sub_test");
        _subscriptionService.GetAsync("sub_test", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(subscription);

        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException("stale xmin token"));

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_test", "cus_test"));

        var act = () => _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Handle_ThroughRetryBehavior_ConcurrencyConflictThenSuccess_UpgradesProAndRetries()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var subscription = CreateMockSubscription("sub_test");
        _subscriptionService.GetAsync("sub_test", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(subscription);

        var saveAttempts = 0;
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => saveAttempts++ == 0
                ? throw new DbUpdateConcurrencyException("stale xmin token")
                : 1);

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_test", "cus_test"));
        var command = new HandleWebhookCommand(json, signature);
        var behavior = new ConcurrencyRetryBehavior<HandleWebhookCommand, Result>(_unitOfWork);
        RequestHandlerDelegate<Result> next = cancellationToken => _handler.Handle(command, cancellationToken);

        var result = await behavior.Handle(command, next, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        saveAttempts.Should().Be(2);
        user.IsPro.Should().BeTrue();
        _unitOfWork.Received(1).ResetTracking();
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_ClearsStalePlayPurchaseToken()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("stale_play_token", DateTime.UtcNow.AddMonths(1));

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var subscription = CreateMockSubscription("sub_test");
        _subscriptionService.GetAsync("sub_test", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(subscription);

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_test", "cus_test"));

        await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        user.PlayPurchaseToken.Should().BeNull();
        user.SubscriptionSource.Should().Be(Orbit.Domain.Enums.SubscriptionSource.Stripe);
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_YearlySubMissingPeriodEnd_FallsBackToOneYearNotOneMonth()
    {
        var user = User.Create("Thomas", "test@example.com").Value;

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var subscription = CreateYearlySubscriptionWithoutPeriodEnd("sub_year");
        _subscriptionService.GetAsync("sub_year", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(subscription);

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_year", "cus_test"));

        await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        user.SubscriptionInterval.Should().Be(Orbit.Domain.Enums.SubscriptionInterval.Yearly);
        user.PlanExpiresAt.Should().NotBeNull();
        user.PlanExpiresAt!.Value.Should().BeCloseTo(DateTime.UtcNow.AddYears(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Handle_SubscriptionCreated_IsSafeNoOp_SetupOwnedByCheckoutSession()
    {
        var (json, signature) = BuildSignedEvent("customer.subscription.created",
            BuildSubscriptionJson("sub_new", "active"));

        var result = await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _userRepo.DidNotReceive().FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvoicePaymentFailed_DoesNotDowngradeProUser()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddMonths(1));
        user.IsPro.Should().BeTrue();

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var (json, signature) = BuildSignedEvent("invoice.payment_failed", BuildInvoiceJson("sub_test"));

        var result = await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.IsPro.Should().BeTrue();
        user.StripeSubscriptionId.Should().Be("sub_test");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_StripeApiErrorFetchingSubscription_ReturnsStripeApiFailure()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        _subscriptionService.GetAsync("sub_test", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException("Stripe service is unavailable"));

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_test", "cus_test"));

        var result = await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Stripe API");
        user.IsPro.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SubscriptionFetchThrowsOperationCanceled_PropagatesNotSwallowedAsFailure()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        _subscriptionService.GetAsync("sub_test", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_test", "cus_test"));

        var act = () => _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_MalformedJsonBody_RejectedGracefullyInsteadOfThrowing()
    {
        const string malformed = "{ not valid json";
        var signature = SignPayload(malformed, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var result = await _handler.Handle(new HandleWebhookCommand(malformed, signature), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("signature");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyBody_RejectedGracefullyInsteadOfThrowing()
    {
        var signature = SignPayload("", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var result = await _handler.Handle(new HandleWebhookCommand("", signature), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("signature");
    }

    [Fact]
    public async Task Handle_SubscriptionUpdated_DataObjectIsNotASubscription_SafeNoOp()
    {
        var (json, signature) = BuildSignedEvent("customer.subscription.updated",
            """{"id":"in_wrongtype","object":"invoice"}""");

        var result = await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _userRepo.DidNotReceive().FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_SubscriptionWithNoItems_FallsBackToMonthlyOneMonth()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        _subscriptionService.GetAsync("sub_noitems", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSubscriptionWithoutItems("sub_noitems"));

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_noitems", "cus_test"));

        await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        user.SubscriptionInterval.Should().Be(Orbit.Domain.Enums.SubscriptionInterval.Monthly);
        user.PlanExpiresAt.Should().NotBeNull();
        user.PlanExpiresAt!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMonths(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Handle_CheckoutSessionCompleted_UnusualIntervalDefaultsToMonthlyButKeepsItemPeriodEnd()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var periodEnd = DateTime.UtcNow.AddDays(7);
        _subscriptionService.GetAsync("sub_weekly", Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSubscriptionWithInterval("sub_weekly", "week", periodEnd));

        var (json, signature) = BuildSignedEvent("checkout.session.completed", BuildCheckoutSessionJson(
            UserId.ToString(), "sub_weekly", "cus_test"));

        await _handler.Handle(new HandleWebhookCommand(json, signature), CancellationToken.None);

        user.SubscriptionInterval.Should().Be(Orbit.Domain.Enums.SubscriptionInterval.Monthly);
        user.PlanExpiresAt!.Value.Should().BeCloseTo(periodEnd, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Handle_ReplayedEventWithOldSignatureTimestamp_RejectedByTolerance()
    {
        var eventJson = BuildEventJson("customer.subscription.updated",
            BuildSubscriptionJson("sub_test", "active"));
        var staleTimestamp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var signature = SignPayload(eventJson, staleTimestamp);

        var result = await _handler.Handle(new HandleWebhookCommand(eventJson, signature), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("signature");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

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

    private static Subscription CreateYearlySubscriptionWithoutPeriodEnd(string subscriptionId)
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
                        Price = new Price
                        {
                            Recurring = new PriceRecurring
                            {
                                Interval = "year",
                                IntervalCount = 1
                            }
                        }
                    }
                ]
            }
        };
    }

    private static Subscription CreateSubscriptionWithoutItems(string subscriptionId) => new()
    {
        Id = subscriptionId,
        Status = "active",
        Items = new StripeList<SubscriptionItem> { Data = [] }
    };

    private static Subscription CreateSubscriptionWithInterval(
        string subscriptionId, string interval, DateTime currentPeriodEnd) => new()
    {
        Id = subscriptionId,
        Status = "active",
        Items = new StripeList<SubscriptionItem>
        {
            Data =
            [
                new SubscriptionItem
                {
                    CurrentPeriodEnd = currentPeriodEnd,
                    Price = new Price
                    {
                        Recurring = new PriceRecurring { Interval = interval, IntervalCount = 1 }
                    }
                }
            ]
        }
    };

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
        var eventJson = BuildEventJson(eventType, dataObjectJson);
        return (eventJson, SignPayload(eventJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    private static string BuildEventJson(string eventType, string dataObjectJson) => $$"""
        {
            "id": "evt_test_{{Guid.NewGuid():N}}",
            "object": "event",
            "api_version": "{{StripeConfiguration.ApiVersion}}",
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

    private static string SignPayload(string body, long timestamp)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(hash)}";
    }

    private sealed class FakeUniqueViolationException : DbException
    {
        public override string SqlState => "23505";
    }
}
