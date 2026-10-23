using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Commands;

public record HandleAppStoreNotificationCommand(string SignedPayload) : IRequest<Result>;

public sealed class HandleAppStoreNotificationCommandHandler(IAppStoreService appStore, IGenericRepository<User> users,
    IGenericRepository<ProcessedAppStoreNotification> notifications, IUnitOfWork unitOfWork)
    : IRequestHandler<HandleAppStoreNotificationCommand, Result>
{
    private static readonly HashSet<string> SupportedTypes = ["SUBSCRIBED", "DID_RENEW", "DID_CHANGE_RENEWAL_STATUS",
        "DID_FAIL_TO_RENEW", "GRACE_PERIOD_EXPIRED", "EXPIRED", "REFUND", "REFUND_REVERSED", "REVOKE"];

    public async Task<Result> Handle(HandleAppStoreNotificationCommand request, CancellationToken cancellationToken)
    {
        if (!appStore.IsConfigured)
            return Result.Failure(ErrorMessages.PaymentServiceUnavailable);
        try
        {
            var notification = appStore.VerifyNotification(request.SignedPayload);
            return await unitOfWork.ExecuteInTransactionAsync(ct => ProcessAsync(notification, ct), cancellationToken);
        }
        catch (AppleVerificationException)
        {
            return Result.Failure(ErrorMessages.InvalidSession);
        }
        catch (BillingProviderException)
        {
            return Result.Failure(ErrorMessages.PaymentServiceUnavailable);
        }
    }

    private async Task<Result> ProcessAsync(AppStoreNotification notification, CancellationToken cancellationToken)
    {
        await unitOfWork.AcquireAdvisoryLockAsync($"app-store-notification:{notification.NotificationUuid}", cancellationToken);
        if (await notifications.AnyAsync(n => n.NotificationUuid == notification.NotificationUuid, cancellationToken))
            return Result.Success();
        if (SupportedTypes.Contains(notification.NotificationType) && notification.Transaction is { } transaction)
        {
            var result = await ReconcileAsync(transaction, cancellationToken);
            if (result.IsFailure)
                return result;
        }
        await notifications.AddAsync(ProcessedAppStoreNotification.Create(notification.NotificationUuid, notification.Environment), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> ReconcileAsync(AppStoreTransaction transaction, CancellationToken cancellationToken)
    {
        await unitOfWork.AcquireAdvisoryLockAsync($"app-store:{transaction.OriginalTransactionId}", cancellationToken);
        var state = await appStore.GetCurrentStateAsync(transaction, cancellationToken);
        if (state is null)
            return Result.Failure(ErrorMessages.PaymentServiceUnavailable);
        await unitOfWork.AcquireUserRowLockAsync(transaction.AppAccountToken, cancellationToken);
        var user = await users.FindOneTrackedIgnoringFiltersAsync(u => u.Id == transaction.AppAccountToken, cancellationToken);
        if (user is null)
            return Result.Success();
        if (state.Transaction.AppAccountToken != user.Id || state.Transaction.OriginalTransactionId != transaction.OriginalTransactionId
            || await users.AnyIgnoringFiltersAsync(u => u.AppStoreOriginalTransactionId == transaction.OriginalTransactionId && u.Id != user.Id, cancellationToken))
            return Result.Failure(ErrorMessages.AppStorePurchaseAccountMismatch);
        if (user.AppStoreOriginalTransactionId is null || user.AppStoreOriginalTransactionId == transaction.OriginalTransactionId)
            AppStoreEntitlement.Apply(user, state);
        return Result.Success();
    }
}
