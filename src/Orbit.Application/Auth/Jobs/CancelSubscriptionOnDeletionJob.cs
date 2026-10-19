using System.Runtime.ExceptionServices;
using Hangfire;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

/// <summary>
/// Commits cancellation ownership before mutating providers. Both phases reload the account under
/// its row lock so delayed cancellation and restoration respect the current account state.
/// </summary>
public sealed class CancelSubscriptionOnDeletionJob(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IBillingService billingService,
    IPlayBillingService playBillingService,
    IOptions<GooglePlaySettings> playSettings)
{
    public static void EnqueueCancellation(IBackgroundJobClient jobs, User user)
    {
        var stripeId = user.StripeSubscriptionId;
        var playToken = user.PlayPurchaseToken;
        if (!string.IsNullOrWhiteSpace(stripeId) || !string.IsNullOrWhiteSpace(playToken))
            jobs.Enqueue<CancelSubscriptionOnDeletionJob>(job =>
                job.ExecuteAsync(user.Id, stripeId, playToken, true, CancellationToken.None));
    }

    public static void EnqueueRestoration(IBackgroundJobClient jobs, User user)
    {
        if (!string.IsNullOrWhiteSpace(user.StripeSubscriptionId) && HasUnexpiredPaidPeriod(user))
            jobs.Enqueue<CancelSubscriptionOnDeletionJob>(job =>
                job.ExecuteAsync(user.Id, user.StripeSubscriptionId, null, false, CancellationToken.None));
    }

    [AutomaticRetry(Attempts = 26, DelaysInSeconds = new[] { 60, 300, 3600 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task ExecuteAsync(Guid userId, string? stripeSubscriptionId, string? playPurchaseToken,
        bool cancelAtPeriodEnd, CancellationToken cancellationToken)
    {
        if (cancelAtPeriodEnd && unitOfWork.HasActiveTransaction)
            throw new InvalidOperationException("Deletion cancellation requires independently committed transactions.");
        var failures = new List<BillingProviderException>();
        if (cancelAtPeriodEnd)
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await unitOfWork.AcquireUserRowLockAsync(userId, ct);
                var user = await userRepository.FindOneTrackedIgnoringFiltersAsync(u => u.Id == userId, ct);
                if (user is not { IsDeactivated: true })
                    return false;

                await PrepareCancellationAsync(user, stripeSubscriptionId, playPurchaseToken, failures, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }, cancellationToken);
            unitOfWork.ResetTracking();
        }

        var accountExists = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await unitOfWork.AcquireUserRowLockAsync(userId, ct);
            var user = await userRepository.FindOneTrackedIgnoringFiltersAsync(u => u.Id == userId, ct);
            if (user is null)
                return false;
            if (user.IsDeactivated && !cancelAtPeriodEnd)
                return true;

            await ReconcileProvidersAsync(user, stripeSubscriptionId, playPurchaseToken,
                user.IsDeactivated, failures, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        if (!accountExists && cancelAtPeriodEnd)
            await CancelProvidersAsync(userId, stripeSubscriptionId, playPurchaseToken, cancellationToken);

        ThrowProviderFailure(failures);
    }

    /// <summary>
    /// Runs the durable cancellation phases before the caller opens its account erasure transaction.
    /// </summary>
    public Task CancelBeforeDeletionAsync(User user, CancellationToken cancellationToken) =>
        ExecuteAsync(user.Id, user.StripeSubscriptionId, user.PlayPurchaseToken, true, cancellationToken);

    private async Task PrepareCancellationAsync(User user, string? stripeId, string? playToken,
        List<BillingProviderException> failures, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(stripeId) && user.StripeSubscriptionId == stripeId && !user.StripeRenewalStoppedByDeletion)
            await CaptureProviderFailureAsync(async () =>
            {
                if (await StripeCanRenewAsync(stripeId, cancellationToken))
                    user.RecordDeletionStoppedRenewal(SubscriptionSource.Stripe, stripeId);
            }, failures);

        if (!string.IsNullOrWhiteSpace(playToken) && user.PlayPurchaseToken == playToken && !user.PlayRenewalStoppedByDeletion)
            await CaptureProviderFailureAsync(async () =>
            {
                if (await PlayCanRenewAsync(playToken, cancellationToken))
                    user.RecordDeletionStoppedRenewal(SubscriptionSource.GooglePlay, playToken);
            }, failures);
    }

    private async Task ReconcileProvidersAsync(User user, string? stripeId, string? playToken,
        bool cancel, List<BillingProviderException> failures, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(stripeId) && user.StripeSubscriptionId == stripeId)
            await CaptureProviderFailureAsync(() => ReconcileStripeAsync(user, stripeId, cancel, cancellationToken), failures);

        if (cancel && !string.IsNullOrWhiteSpace(playToken) && user.PlayPurchaseToken == playToken && user.PlayRenewalStoppedByDeletion)
            await CaptureProviderFailureAsync(() => CancelPlayAsync(playToken, cancellationToken), failures);
    }

    private async Task ReconcileStripeAsync(User user, string stripeId, bool cancel, CancellationToken cancellationToken)
    {
        if (!user.StripeRenewalStoppedByDeletion)
            return;

        var subscription = await billingService.GetSubscriptionDetailsAsync(stripeId, cancellationToken);
        if (subscription is null || subscription.Status is "canceled" or "incomplete_expired"
            || subscription.CancelAtPeriodEnd && subscription.DeletionOwner != user.Id.ToString())
        {
            user.ReleaseStripeDeletionCancellation(stripeId);
            return;
        }

        if (cancel && !subscription.CancelAtPeriodEnd)
            await billingService.SetSubscriptionCancelAtPeriodEndAsync(stripeId, true, cancellationToken, user.Id.ToString());
        else if (!cancel && !subscription.CancelAtPeriodEnd)
            user.ReleaseStripeDeletionCancellation(stripeId);
        else if (!cancel && HasUnexpiredPaidPeriod(user))
        {
            await billingService.SetSubscriptionCancelAtPeriodEndAsync(stripeId, false, cancellationToken);
            user.RecordStripeRenewalRestored(stripeId);
        }
    }

    private static async Task CaptureProviderFailureAsync(Func<Task> operation, List<BillingProviderException> failures)
    {
        try
        {
            await operation();
        }
        catch (BillingProviderException ex)
        {
            failures.Add(ex);
        }
    }

    private static void ThrowProviderFailure(List<BillingProviderException> failures)
    {
        if (failures.Count > 0)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
    }

    private static bool HasUnexpiredPaidPeriod(User user)
    {
        var nowAtUtc = DateTime.UtcNow;
        return user.IsPro && user.PlanExpiresAt > nowAtUtc;
    }

    private Task CancelProvidersAsync(Guid userId, string? stripeSubscriptionId, string? playPurchaseToken,
        CancellationToken cancellationToken) =>
        Task.WhenAll(CancelStripeAsync(userId, stripeSubscriptionId, cancellationToken),
            CancelPlayAsync(playPurchaseToken, cancellationToken));

    private async Task CancelStripeAsync(Guid userId, string? stripeSubscriptionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stripeSubscriptionId) || !await StripeCanRenewAsync(stripeSubscriptionId, cancellationToken))
            return;
        await billingService.SetSubscriptionCancelAtPeriodEndAsync(stripeSubscriptionId, true, cancellationToken, userId.ToString());
    }

    private async Task<bool> StripeCanRenewAsync(string stripeSubscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await billingService.GetSubscriptionDetailsAsync(stripeSubscriptionId, cancellationToken);
        return subscription is not null && subscription.Status is not ("canceled" or "incomplete_expired") && !subscription.CancelAtPeriodEnd;
    }

    private async Task CancelPlayAsync(string? playPurchaseToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playPurchaseToken) || !await PlayCanRenewAsync(playPurchaseToken, cancellationToken))
            return;
        await playBillingService.CancelAsync(playPurchaseToken, cancellationToken);
    }

    private async Task<bool> PlayCanRenewAsync(string playPurchaseToken, CancellationToken cancellationToken)
    {
        var purchase = await playBillingService.VerifyAsync(playSettings.Value.ProductId, playPurchaseToken, cancellationToken);
        return purchase is { CanRenew: true };
    }
}
