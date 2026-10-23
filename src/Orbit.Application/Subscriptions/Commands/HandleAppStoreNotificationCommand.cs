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
        "DID_FAIL_TO_RENEW", "GRACE_PERIOD_EXPIRED", "EXPIRED", "REFUND", "REVOKE"];

    public async Task<Result> Handle(HandleAppStoreNotificationCommand request, CancellationToken cancellationToken)
    {
        if (!appStore.IsConfigured)
            return Result.Failure(ErrorMessages.PaymentServiceUnavailable);
        try
        {
            var notification = appStore.VerifyNotification(request.SignedPayload);
            return await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"app-store-notification:{notification.NotificationUuid}", ct);
                if (await notifications.AnyAsync(n => n.NotificationUuid == notification.NotificationUuid, ct))
                    return Result.Success();
                if (SupportedTypes.Contains(notification.NotificationType) && notification.Transaction is { } transaction)
                {
                    await unitOfWork.AcquireAdvisoryLockAsync($"app-store:{transaction.OriginalTransactionId}", ct);
                    var state = await appStore.GetCurrentStateAsync(transaction, ct);
                    if (state is null)
                        return Result.Failure(ErrorMessages.PaymentServiceUnavailable);
                    await unitOfWork.AcquireUserRowLockAsync(transaction.AppAccountToken, ct);
                    var user = await users.FindOneTrackedIgnoringFiltersAsync(u => u.Id == transaction.AppAccountToken, ct);
                    if (user is not null)
                    {
                        if (state.Transaction.AppAccountToken != user.Id
                            || await users.AnyIgnoringFiltersAsync(u => u.AppStoreOriginalTransactionId == transaction.OriginalTransactionId && u.Id != user.Id, ct))
                            return Result.Failure(ErrorMessages.PlayPurchaseAccountMismatch);
                        if (user.AppStoreOriginalTransactionId is null || user.AppStoreOriginalTransactionId == transaction.OriginalTransactionId)
                            AppStoreEntitlement.Apply(user, state);
                    }
                }
                await notifications.AddAsync(ProcessedAppStoreNotification.Create(notification.NotificationUuid, notification.Environment), ct);
                await unitOfWork.SaveChangesAsync(ct);
                return Result.Success();
            }, cancellationToken);
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
}
