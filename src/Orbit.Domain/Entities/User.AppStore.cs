using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public partial class User
{
    public string? AppStoreOriginalTransactionId { get; private set; }
    public string? AppStoreEnvironment { get; private set; }
    public DateTime? AppStoreStateSignedAtUtc { get; private set; }

    public void LinkAppStoreSubscription(string originalTransactionId, string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalTransactionId);
        if (originalTransactionId.Length > 255 || environment is not ("Sandbox" or "Production"))
            throw new ArgumentException("App Store subscription identifiers or environment are invalid.");
        if (AppStoreOriginalTransactionId != originalTransactionId)
            AppStoreStateSignedAtUtc = null;
        AppStoreOriginalTransactionId = originalTransactionId;
        AppStoreEnvironment = environment;
    }

    public void ApplyAppStoreSubscription(string originalTransactionId, string environment, DateTime expiresAtUtc,
        SubscriptionInterval interval, DateTime signedAtUtc, bool grantsPro, SubscriptionLapseReason? lapseReason)
    {
        if (expiresAtUtc.Kind != DateTimeKind.Utc || signedAtUtc.Kind != DateTimeKind.Utc || !Enum.IsDefined(interval)
            || lapseReason.HasValue && !Enum.IsDefined(lapseReason.Value))
            throw new ArgumentException("App Store state must have UTC instants and valid subscription values.");
        LinkAppStoreSubscription(originalTransactionId, environment);
        if (AppStoreStateSignedAtUtc > signedAtUtc)
            return;
        AppStoreStateSignedAtUtc = signedAtUtc;
        if (SubscriptionSource == Enums.SubscriptionSource.Stripe && HasActivePaidSubscription && SubscriptionExpiresAtUtc > expiresAtUtc)
            return;
        if (!grantsPro)
        {
            if (SubscriptionSource == Enums.SubscriptionSource.AppStore)
                ClearSubscriptionEntitlement(lapseReason ?? Enums.SubscriptionLapseReason.Expired);
            return;
        }
        SubscriptionSource = Enums.SubscriptionSource.AppStore;
        SubscriptionPlan = UserPlan.Pro;
        SubscriptionExpiresAtUtc = expiresAtUtc;
        SubscriptionInterval = interval;
        SubscriptionLapseReason = lapseReason;
        SubscriptionEndedAtUtc = null;
    }
}
