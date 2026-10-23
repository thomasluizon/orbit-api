using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class AppStoreCommandTests
{
    private readonly IAppStoreService _apple = Substitute.For<IAppStoreService>();
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<ProcessedAppStoreNotification> _notifications = Substitute.For<IGenericRepository<ProcessedAppStoreNotification>>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly User _user = User.Create("Apple", "apple@example.com").Value;
    private AppStoreTransaction Transaction => new("original", "latest", _user.Id, "Sandbox", "pro.monthly", DateTime.UtcNow.AddDays(30), DateTime.UtcNow, false, SubscriptionInterval.Monthly);

    public AppStoreCommandTests()
    {
        _apple.IsConfigured.Returns(true);
        _apple.VerifyTransaction("signed").Returns(_ => Transaction);
        _apple.GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>())
            .Returns(call => { var transaction = call.Arg<AppStoreTransaction>(); return new AppStoreSubscriptionState(transaction, true, transaction.ExpiresAtUtc, transaction.SignedAtUtc, false, true); });
        _users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(_user);
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(_user);
        _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<AppStoreVerifyResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<AppStoreVerifyResponse>>>>()(default));
        _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result>>>()(default));
    }

    [Fact]
    public async Task Verify_GrantsAndPreservesOldStrictSourceSchema()
    {
        var result = await new VerifyAppStorePurchaseCommandHandler(_apple, _users, _work).Handle(new(_user.Id, "signed"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.HasProAccess.Should().BeTrue();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, JsonSerializerOptions.Web));
        json.RootElement.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("billingProvider").GetString().Should().Be("app_store");
        _user.SubscriptionSource.ToApiValue().Should().BeNull();
        await _work.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verify_WrongAccountOrOriginalAlreadyOwned_Rejects(bool alreadyOwned)
    {
        if (alreadyOwned)
            _users.AnyIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(true);
        else
            _apple.VerifyTransaction("signed").Returns(Transaction with { AppAccountToken = Guid.NewGuid() });
        var result = await new VerifyAppStorePurchaseCommandHandler(_apple, _users, _work).Handle(new(_user.Id, "signed"), default);
        result.ErrorCode.Should().Be(ErrorCodes.PlayPurchaseAccountMismatch);
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _user.AppStoreOriginalTransactionId.Should().BeNull();
    }

    [Fact]
    public async Task Verify_BadSignature_ChangesNothing()
    {
        _apple.VerifyTransaction("signed").Throws(new AppleVerificationException());
        var result = await new VerifyAppStorePurchaseCommandHandler(_apple, _users, _work).Handle(new(_user.Id, "signed"), default);
        result.IsFailure.Should().BeTrue();
        _user.AppStoreOriginalTransactionId.Should().BeNull();
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("SUBSCRIBED")]
    [InlineData("DID_RENEW")]
    [InlineData("DID_CHANGE_RENEWAL_STATUS")]
    [InlineData("DID_FAIL_TO_RENEW")]
    [InlineData("GRACE_PERIOD_EXPIRED")]
    [InlineData("EXPIRED")]
    [InlineData("REFUND")]
    [InlineData("REVOKE")]
    public async Task Notification_UsesCurrentStateAndDeduplicates(string type)
    {
        var transaction = Transaction;
        _user.StartTrial(DateTime.UtcNow.AddDays(-30));
        _user.ApplyAppStoreSubscription(transaction.OriginalTransactionId, transaction.Environment, transaction.ExpiresAtUtc,
            transaction.Interval, transaction.SignedAtUtc.AddSeconds(-1), true, null);
        var removes = type is "REFUND" or "REVOKE" or "EXPIRED" or "GRACE_PERIOD_EXPIRED";
        var notification = new AppStoreNotification(Guid.NewGuid(), type, "Sandbox", transaction);
        _apple.VerifyNotification("signed").Returns(notification);
        _apple.GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>()).Returns(
            new AppStoreSubscriptionState(transaction, !removes, transaction.ExpiresAtUtc, transaction.SignedAtUtc, false, true));
        var processed = false;
        _notifications.AnyAsync(Arg.Any<Expression<Func<ProcessedAppStoreNotification, bool>>>(), Arg.Any<CancellationToken>()).Returns(_ => processed);
        _notifications.AddAsync(Arg.Any<ProcessedAppStoreNotification>(), Arg.Any<CancellationToken>()).Returns(_ => { processed = true; return Task.CompletedTask; });
        var handler = new HandleAppStoreNotificationCommandHandler(_apple, _users, _notifications, _work);
        (await handler.Handle(new("signed"), default)).IsSuccess.Should().BeTrue();
        _user.HasProAccess.Should().Be(!removes);
        (await handler.Handle(new("signed"), default)).IsSuccess.Should().BeTrue();
        await _apple.Received(1).GetCurrentStateAsync(Arg.Any<AppStoreTransaction>(), Arg.Any<CancellationToken>());
        await _work.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Validators_RejectMissingInput()
    {
        new VerifyAppStorePurchaseCommandValidator().Validate(new VerifyAppStorePurchaseCommand(Guid.Empty, "")).IsValid.Should().BeFalse();
        new HandleAppStoreNotificationCommandValidator().Validate(new HandleAppStoreNotificationCommand("")).IsValid.Should().BeFalse();
    }
}
