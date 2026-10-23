using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public partial class AppStoreCommandTests
{
    [Theory]
    [InlineData("disabled", ErrorCodes.PaymentServiceUnavailable)]
    [InlineData("inactive", ErrorCodes.AppStorePurchaseNotActive)]
    [InlineData("no-state", ErrorCodes.AppStorePurchaseNotActive)]
    [InlineData("account", ErrorCodes.AppStorePurchaseAccountMismatch)]
    [InlineData("original", ErrorCodes.AppStorePurchaseAccountMismatch)]
    [InlineData("provider", ErrorCodes.PaymentServiceUnavailable)]
    [InlineData("missing-user", ErrorCodes.UserNotFound)]
    public async Task Verify_UnusablePurchase_DoesNotSave(string failure, string error)
    {
        var transaction = Transaction;
        var current = transaction;
        if (failure == "disabled") _apple.IsConfigured.Returns(false);
        if (failure == "account") current = transaction with { AppAccountToken = Guid.NewGuid() };
        if (failure == "original") current = transaction with { OriginalTransactionId = "another-original" };
        _apple.GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>())
            .Returns(failure == "no-state" ? null : new AppStoreSubscriptionState(current, failure != "inactive", current.ExpiresAtUtc, current.SignedAtUtc, false, true));
        if (failure == "provider")
            _apple.GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>()).ThrowsAsync(new BillingProviderException("Unavailable"));
        if (failure == "missing-user")
            _users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        var result = await new VerifyAppStorePurchaseCommandHandler(_apple, _users, _work).Handle(new(_user.Id, "signed"), default);
        result.ErrorCode.Should().Be(error);
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _user.AppStoreOriginalTransactionId.Should().BeNull();
    }

    [Theory]
    [InlineData("disabled", ErrorCodes.PaymentServiceUnavailable)]
    [InlineData("signature", ErrorCodes.InvalidSession)]
    [InlineData("provider", ErrorCodes.PaymentServiceUnavailable)]
    [InlineData("no-state", ErrorCodes.PaymentServiceUnavailable)]
    [InlineData("account", ErrorCodes.AppStorePurchaseAccountMismatch)]
    [InlineData("owned", ErrorCodes.AppStorePurchaseAccountMismatch)]
    public async Task Notification_FailedReconciliation_IsNotAcknowledgedAsProcessed(string failure, string error)
    {
        var transaction = Transaction;
        _apple.VerifyNotification("signed").Returns(new AppStoreNotification(Guid.NewGuid(), "REFUND_REVERSED", "Sandbox", transaction));
        if (failure == "disabled") _apple.IsConfigured.Returns(false);
        if (failure == "signature") _apple.VerifyNotification("signed").Throws(new AppleVerificationException());
        if (failure == "provider") _apple.GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>()).ThrowsAsync(new BillingProviderException("Unavailable"));
        if (failure == "no-state") _apple.GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>()).Returns((AppStoreSubscriptionState?)null);
        if (failure == "account") _apple.GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>()).Returns(
            new AppStoreSubscriptionState(transaction with { AppAccountToken = Guid.NewGuid() }, true, transaction.ExpiresAtUtc, transaction.SignedAtUtc, false, true));
        if (failure == "owned") _users.AnyIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(true);
        var result = await new HandleAppStoreNotificationCommandHandler(_apple, _users, _notifications, _work).Handle(new("signed"), default);
        result.ErrorCode.Should().Be(error);
        await _notifications.DidNotReceive().AddAsync(Arg.Any<ProcessedAppStoreNotification>(), Arg.Any<CancellationToken>());
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("TEST")]
    [InlineData("UNSUPPORTED")]
    public async Task Notification_WithoutSubscriptionReconciliation_RecordsUuid(string type)
    {
        _apple.VerifyNotification("signed").Returns(new AppStoreNotification(Guid.NewGuid(), type, "Sandbox", null));
        (await new HandleAppStoreNotificationCommandHandler(_apple, _users, _notifications, _work).Handle(new("signed"), default)).IsSuccess.Should().BeTrue();
        await _apple.DidNotReceive().GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>());
        await _work.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notification_DeletedUser_RecordsUuidWithoutGranting()
    {
        _apple.VerifyNotification("signed").Returns(new AppStoreNotification(Guid.NewGuid(), "DID_RENEW", "Sandbox", Transaction));
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        (await new HandleAppStoreNotificationCommandHandler(_apple, _users, _notifications, _work).Handle(new("signed"), default)).IsSuccess.Should().BeTrue();
        await _work.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, true, SubscriptionLapseReason.PaymentFailed)]
    [InlineData(false, false, SubscriptionLapseReason.Canceled)]
    public async Task Verify_GraceAndRenewalDisabled_PreserveLapseReason(bool grace, bool autoRenews, SubscriptionLapseReason expected)
    {
        var transaction = Transaction;
        _apple.GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>()).Returns(
            new AppStoreSubscriptionState(transaction, true, transaction.ExpiresAtUtc, transaction.SignedAtUtc, grace, autoRenews));
        (await new VerifyAppStorePurchaseCommandHandler(_apple, _users, _work).Handle(new(_user.Id, "signed"), default)).IsSuccess.Should().BeTrue();
        _user.SubscriptionLapseReason.Should().Be(expected);
    }
}
