using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public sealed class AccountReactivation(
    IGenericRepository<Notification> notifications,
    IGenericRepository<OwedSubscriptionCancellation> owedCancellations)
{
    public async Task ReactivateAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.IsDeactivated)
            return;

        if (user.PlayRenewalStoppedByDeletion)
        {
            var isPortuguese = LocaleHelper.IsPortuguese(user.Language);
            var title = isPortuguese
                ? "Sua renovação no Google Play está desativada"
                : "Your Google Play renewal is off";
            var body = isPortuguese
                ? "Sua conta do Orbit está ativa de novo. A renovação no Google Play foi desativada quando você pediu a exclusão da conta. Para voltar a renovar, abra o Google Play, vá em Pagamentos e assinaturas > Assinaturas, escolha Orbit e toque em Assinar novamente. Se essa opção não aparecer, assine o Pro de novo pelo Orbit."
                : "Your Orbit account is active again. Deleting your account turned off your Google Play renewal. To renew again, open Google Play, go to Payments & subscriptions > Subscriptions, select Orbit, and tap Resubscribe. If that option is unavailable, subscribe to Pro again in Orbit.";
            await notifications.AddAsync(Notification.Create(user.Id, title, body), cancellationToken);
            await owedCancellations.DeleteAsync(record => record.Provider == SubscriptionSource.GooglePlay
                && record.ProviderIdentifier == user.PlayPurchaseToken, cancellationToken);
        }

        user.CancelDeactivation();
    }
}
