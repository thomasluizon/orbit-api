using Orbit.Domain.Enums;

namespace Orbit.Application.Common;

public interface IAppStoreService
{
    bool IsConfigured { get; }
    AppStoreTransaction VerifyTransaction(string signedTransaction);
    AppStoreNotification VerifyNotification(string signedPayload);
    Task<AppStoreSubscriptionState?> GetCurrentStateAsync(AppStoreTransaction transaction, CancellationToken cancellationToken);
}

public sealed record AppStoreTransaction(string OriginalTransactionId, string TransactionId, Guid AppAccountToken,
    string Environment, string ProductId, DateTime ExpiresAtUtc, DateTime SignedAtUtc, bool IsRevoked, SubscriptionInterval Interval);

public sealed record AppStoreSubscriptionState(AppStoreTransaction Transaction, bool GrantsPro, DateTime ExpiresAtUtc,
    DateTime SignedAtUtc, bool IsInGracePeriod, bool AutoRenews);

public sealed record AppStoreNotification(Guid NotificationUuid, string NotificationType, string Environment, AppStoreTransaction? Transaction);

public sealed class AppleVerificationException : Exception
{
    public AppleVerificationException() : base("Apple signature or claims are invalid.") { }
}
