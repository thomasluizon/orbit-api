using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Commands;

public record AppStoreVerifyResponse(bool HasProAccess, string? Source, string? SubscriptionInterval, DateTime? PlanExpiresAt, string? BillingProvider = null);
public record VerifyAppStorePurchaseCommand(Guid UserId, string SignedTransaction) : IRequest<Result<AppStoreVerifyResponse>>;

public sealed class VerifyAppStorePurchaseCommandHandler(
    IAppStoreService appStore, IGenericRepository<User> users, IUnitOfWork unitOfWork)
    : IRequestHandler<VerifyAppStorePurchaseCommand, Result<AppStoreVerifyResponse>>
{
    public async Task<Result<AppStoreVerifyResponse>> Handle(VerifyAppStorePurchaseCommand request, CancellationToken cancellationToken)
    {
        if (!appStore.IsConfigured)
            return Result.Failure<AppStoreVerifyResponse>(ErrorMessages.PaymentServiceUnavailable);
        try
        {
            var transaction = appStore.VerifyTransaction(request.SignedTransaction);
            if (transaction.AppAccountToken != request.UserId)
                return Result.Failure<AppStoreVerifyResponse>(ErrorMessages.AppStorePurchaseAccountMismatch);
            var state = await appStore.GetCurrentStateAsync(transaction, cancellationToken);
            if (state is null || !state.GrantsPro)
                return Result.Failure<AppStoreVerifyResponse>(ErrorMessages.AppStorePurchaseNotActive);
            if (state.Transaction.AppAccountToken != request.UserId || state.Transaction.OriginalTransactionId != transaction.OriginalTransactionId)
                return Result.Failure<AppStoreVerifyResponse>(ErrorMessages.AppStorePurchaseAccountMismatch);
            return await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"app-store:{transaction.OriginalTransactionId}", ct);
                await unitOfWork.AcquireUserRowLockAsync(request.UserId, ct);
                var user = await users.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: ct);
                if (user is null)
                    return Result.Failure<AppStoreVerifyResponse>(ErrorMessages.UserNotFound);
                if (await users.AnyIgnoringFiltersAsync(u => u.AppStoreOriginalTransactionId == transaction.OriginalTransactionId && u.Id != user.Id, ct))
                    return Result.Failure<AppStoreVerifyResponse>(ErrorMessages.AppStorePurchaseAccountMismatch);
                AppStoreEntitlement.Apply(user, state);
                await unitOfWork.SaveChangesAsync(ct);
                return Result.Success(new AppStoreVerifyResponse(user.HasProAccess, user.SubscriptionSource.ToApiValue(),
                    user.SubscriptionInterval?.ToString().ToLowerInvariant(), user.PlanExpiresAt, user.SubscriptionSource.ToBillingProvider()));
            }, cancellationToken);
        }
        catch (AppleVerificationException)
        {
            return Result.Failure<AppStoreVerifyResponse>(ErrorMessages.AppStorePurchaseNotActive);
        }
        catch (BillingProviderException)
        {
            return Result.Failure<AppStoreVerifyResponse>(ErrorMessages.PaymentServiceUnavailable);
        }
    }
}

internal static class AppStoreEntitlement
{
    internal static void Apply(User user, AppStoreSubscriptionState state)
    {
        var transaction = state.Transaction;
        var reason = state.IsInGracePeriod ? SubscriptionLapseReason.PaymentFailed
            : !state.GrantsPro ? SubscriptionLapseReason.Expired : !state.AutoRenews ? SubscriptionLapseReason.Canceled : (SubscriptionLapseReason?)null;
        user.ApplyAppStoreSubscription(transaction.OriginalTransactionId, transaction.Environment, state.ExpiresAtUtc,
            transaction.Interval, state.SignedAtUtc, state.GrantsPro, reason);
    }
}
