using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public sealed class AccountReactivation(
    IGenericRepository<OwedSubscriptionCancellation> owedCancellations)
{
    public async Task<string?> ReactivateAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.IsDeactivated)
            return null;

        var playToken = user.PlayRenewalStoppedByDeletion ? user.PlayPurchaseToken : null;
        if (playToken is not null)
        {
            await owedCancellations.DeleteAsync(record => record.Provider == SubscriptionSource.GooglePlay
                && record.ProviderIdentifier == playToken, cancellationToken);
        }

        user.CancelDeactivation();
        return playToken;
    }
}
