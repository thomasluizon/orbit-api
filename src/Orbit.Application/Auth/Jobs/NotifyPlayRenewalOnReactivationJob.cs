using Hangfire;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

public sealed class NotifyPlayRenewalOnReactivationJob(
    IGenericRepository<User> users,
    IGenericRepository<Notification> notifications,
    IUnitOfWork unitOfWork,
    IPlayBillingService play,
    IOptions<GooglePlaySettings> playSettings)
{
    public static void Enqueue(IBackgroundJobClient jobs, Guid userId, string? purchaseToken)
    {
        if (string.IsNullOrWhiteSpace(purchaseToken))
            return;

        var reactivationId = Guid.NewGuid();
        jobs.Enqueue<NotifyPlayRenewalOnReactivationJob>(job =>
            job.ExecuteAsync(userId, purchaseToken, reactivationId, CancellationToken.None));
    }

    [AutomaticRetry(Attempts = 26, DelaysInSeconds = new[] { 60, 300, 3600 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task ExecuteAsync(Guid userId, string purchaseToken, Guid reactivationId, CancellationToken cancellationToken)
    {
        if (unitOfWork.HasActiveTransaction)
            throw new InvalidOperationException("Play renewal verification requires no active transaction.");

        var purchase = await play.VerifyAsync(playSettings.Value.ProductId, purchaseToken, cancellationToken);
        if (purchase is not { CanRenew: false })
            return;

        unitOfWork.ResetTracking();
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await unitOfWork.AcquireUserRowLockAsync(userId, ct);
            var user = await users.FindOneTrackedIgnoringFiltersAsync(user => user.Id == userId, ct);
            if (user is null || user.IsDeactivated || user.PlayPurchaseToken != purchaseToken)
                return;

            var dedupeKey = $"play-reactivation:{userId}:{reactivationId}";
            if (await notifications.FindOneTrackedIgnoringFiltersAsync(notice => notice.DedupeKey == dedupeKey, ct) is not null)
                return;

            var isPortuguese = LocaleHelper.IsPortuguese(user.Language);
            var title = isPortuguese
                ? "Sua renovação no Google Play está desativada"
                : "Your Google Play renewal is off";
            var body = isPortuguese
                ? "Sua conta do Orbit está ativa de novo. A renovação no Google Play foi desativada quando você pediu a exclusão da conta. Para voltar a renovar, abra o Google Play, vá em Pagamentos e assinaturas > Assinaturas, escolha Orbit e toque em Assinar novamente. Se essa opção não aparecer, assine o Pro de novo pelo Orbit."
                : "Your Orbit account is active again. Deleting your account turned off your Google Play renewal. To renew again, open Google Play, go to Payments & subscriptions > Subscriptions, select Orbit, and tap Resubscribe. If that option is unavailable, subscribe to Pro again in Orbit.";
            await notifications.AddAsync(Notification.Create(user.Id, title, body, dedupeKey: dedupeKey), ct);
            await unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);
    }
}
