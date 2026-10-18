using System.Runtime.ExceptionServices;
using Hangfire;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

/// <summary>
/// Persists renewal changes as primitive Hangfire arguments. Retries reconcile the current account
/// state under its row lock so delayed cancellation and restoration cannot reverse a later sign-in or deletion.
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
        var failures = new List<BillingProviderException>();
        var accountExists = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await unitOfWork.AcquireUserRowLockAsync(userId, ct);
            var user = await userRepository.FindOneTrackedIgnoringFiltersAsync(u => u.Id == userId, ct);
            if (user is null)
                return false;
            if (user.IsDeactivated != cancelAtPeriodEnd)
                return true;

            await ReconcileProvidersAsync(user, stripeSubscriptionId, playPurchaseToken, cancelAtPeriodEnd, failures, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        if (!accountExists && cancelAtPeriodEnd)
            await CancelProvidersAsync(stripeSubscriptionId, playPurchaseToken, cancellationToken);

        ThrowProviderFailure(failures);
    }

    /// <summary>
    /// The hard-delete backstop checks both retained provider identifiers. The executor commits
    /// successful cancellation ownership even if another provider fails and erasure must wait.
    /// </summary>
    public async Task CancelBeforeDeletionAsync(User user, CancellationToken cancellationToken)
    {
        var failures = new List<BillingProviderException>();
        await ReconcileProvidersAsync(user, user.StripeSubscriptionId, user.PlayPurchaseToken, true, failures, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        ThrowProviderFailure(failures);
    }

    private async Task ReconcileProvidersAsync(User user, string? stripeId, string? playToken,
        bool cancel, List<BillingProviderException> failures, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(stripeId) && user.StripeSubscriptionId == stripeId)
            await CaptureProviderFailureAsync(() => ReconcileStripeAsync(user, stripeId, cancel, cancellationToken), failures);

        if (cancel && !string.IsNullOrWhiteSpace(playToken) && user.PlayPurchaseToken == playToken)
            await CaptureProviderFailureAsync(async () =>
            {
                if (await CancelPlayAsync(playToken, cancellationToken))
                    user.RecordDeletionStoppedRenewal(SubscriptionSource.GooglePlay, playToken);
            }, failures);
    }

    private async Task ReconcileStripeAsync(User user, string stripeId, bool cancel, CancellationToken cancellationToken)
    {
        if (cancel)
        {
            if (await CancelStripeAsync(stripeId, cancellationToken))
                user.RecordDeletionStoppedRenewal(SubscriptionSource.Stripe, stripeId);
        }
        else if (user.StripeRenewalStoppedByDeletion && HasUnexpiredPaidPeriod(user))
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

    private Task CancelProvidersAsync(string? stripeSubscriptionId, string? playPurchaseToken,
        CancellationToken cancellationToken) =>
        Task.WhenAll(CancelStripeAsync(stripeSubscriptionId, cancellationToken),
            CancelPlayAsync(playPurchaseToken, cancellationToken));

    private async Task<bool> CancelStripeAsync(string? stripeSubscriptionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stripeSubscriptionId))
            return false;
        var subscription = await billingService.GetSubscriptionDetailsAsync(stripeSubscriptionId, cancellationToken);
        if (subscription is null || subscription.Status is "canceled" or "incomplete_expired" || subscription.CancelAtPeriodEnd)
            return false;

        await billingService.SetSubscriptionCancelAtPeriodEndAsync(stripeSubscriptionId, true, cancellationToken);
        return true;
    }

    private async Task<bool> CancelPlayAsync(string? playPurchaseToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playPurchaseToken))
            return false;
        var purchase = await playBillingService.VerifyAsync(playSettings.Value.ProductId, playPurchaseToken, cancellationToken);
        if (purchase is not { CanRenew: true })
            return false;

        await playBillingService.CancelAsync(playPurchaseToken, cancellationToken);
        return true;
    }
}
