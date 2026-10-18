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
        var stripeId = user.SubscriptionSource == SubscriptionSource.Stripe ? user.StripeSubscriptionId : null;
        var playToken = user.SubscriptionSource == SubscriptionSource.GooglePlay ? user.PlayPurchaseToken : null;
        if (!string.IsNullOrWhiteSpace(stripeId) || !string.IsNullOrWhiteSpace(playToken))
            jobs.Enqueue<CancelSubscriptionOnDeletionJob>(job =>
                job.ExecuteAsync(user.Id, stripeId, playToken, true, CancellationToken.None));
    }

    public static void EnqueueRestoration(IBackgroundJobClient jobs, User user)
    {
        if (user.SubscriptionSource == SubscriptionSource.Stripe
            && !string.IsNullOrWhiteSpace(user.StripeSubscriptionId) && HasUnexpiredPaidPeriod(user))
            jobs.Enqueue<CancelSubscriptionOnDeletionJob>(job =>
                job.ExecuteAsync(user.Id, user.StripeSubscriptionId, null, false, CancellationToken.None));
    }

    [AutomaticRetry(Attempts = 26, DelaysInSeconds = new[] { 60, 300, 3600 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task ExecuteAsync(Guid userId, string? stripeSubscriptionId, string? playPurchaseToken,
        bool cancelAtPeriodEnd, CancellationToken cancellationToken)
    {
        var accountDeleted = false;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await unitOfWork.AcquireUserRowLockAsync(userId, ct);
            var user = await userRepository.FindOneTrackedIgnoringFiltersAsync(u => u.Id == userId, ct);
            if (user is null)
            {
                accountDeleted = true;
                return;
            }
            if (user.IsDeactivated != cancelAtPeriodEnd)
                return;

            if (!string.IsNullOrWhiteSpace(stripeSubscriptionId)
                && user.SubscriptionSource == SubscriptionSource.Stripe
                && user.StripeSubscriptionId == stripeSubscriptionId)
            {
                if (cancelAtPeriodEnd)
                    await CancelProvidersAsync(stripeSubscriptionId, null, ct);
                else if (HasUnexpiredPaidPeriod(user))
                    await billingService.SetSubscriptionCancelAtPeriodEndAsync(stripeSubscriptionId, false, ct);
            }

            if (cancelAtPeriodEnd && !string.IsNullOrWhiteSpace(playPurchaseToken)
                && user.SubscriptionSource == SubscriptionSource.GooglePlay
                && user.PlayPurchaseToken == playPurchaseToken)
                await CancelProvidersAsync(null, playPurchaseToken, ct);
        }, cancellationToken);

        if (accountDeleted && cancelAtPeriodEnd)
            await CancelProvidersAsync(stripeSubscriptionId, playPurchaseToken, cancellationToken);
    }

    /// <summary>
    /// The hard-delete backstop checks both retained provider identifiers, including an older provider
    /// after a billing-source switch. Provider failures propagate so the executor can defer erasure
    /// or persist cancellation arguments before removing an account whose deferral limit has elapsed.
    /// </summary>
    public Task CancelBeforeDeletionAsync(User user, CancellationToken cancellationToken) =>
        CancelProvidersAsync(user.StripeSubscriptionId, user.PlayPurchaseToken, cancellationToken);

    private static bool HasUnexpiredPaidPeriod(User user)
    {
        var nowAtUtc = DateTime.UtcNow;
        return user.IsPro && user.PlanExpiresAt > nowAtUtc;
    }

    private Task CancelProvidersAsync(string? stripeSubscriptionId, string? playPurchaseToken,
        CancellationToken cancellationToken) =>
        Task.WhenAll(CancelStripeAsync(stripeSubscriptionId, cancellationToken),
            CancelPlayAsync(playPurchaseToken, cancellationToken));

    private async Task CancelStripeAsync(string? stripeSubscriptionId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(stripeSubscriptionId))
        {
            var subscription = await billingService.GetSubscriptionDetailsAsync(stripeSubscriptionId, cancellationToken);
            if (subscription is not null && subscription.Status is not ("canceled" or "incomplete_expired")
                && !subscription.CancelAtPeriodEnd)
                await billingService.SetSubscriptionCancelAtPeriodEndAsync(stripeSubscriptionId, true, cancellationToken);
        }
    }

    private async Task CancelPlayAsync(string? playPurchaseToken, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(playPurchaseToken))
        {
            var purchase = await playBillingService.VerifyAsync(playSettings.Value.ProductId, playPurchaseToken, cancellationToken);
            if (purchase is { IsActive: true })
                await playBillingService.CancelAsync(playPurchaseToken, cancellationToken);
        }
    }
}
