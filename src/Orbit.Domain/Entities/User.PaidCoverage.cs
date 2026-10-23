using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public partial class User
{
    public DateTime? StripeCoverageExpiresAtUtc { get; private set; }
    public SubscriptionInterval? StripeCoverageInterval { get; private set; }
    public SubscriptionLapseReason? StripeCoverageLapseReason { get; private set; }
    public DateTime? PlayCoverageExpiresAtUtc { get; private set; }
    public SubscriptionInterval? PlayCoverageInterval { get; private set; }
    public SubscriptionLapseReason? PlayCoverageLapseReason { get; private set; }
    public DateTime? AppStoreCoverageExpiresAtUtc { get; private set; }
    public SubscriptionInterval? AppStoreCoverageInterval { get; private set; }
    public SubscriptionLapseReason? AppStoreCoverageLapseReason { get; private set; }

    private void RememberPaidCoverage()
    {
        if (SubscriptionSource is { } source)
            StorePaidCoverage(source, SubscriptionExpiresAtUtc, SubscriptionInterval, SubscriptionLapseReason);
    }

    private void StorePaidCoverage(SubscriptionSource source, DateTime? expiresAtUtc,
        SubscriptionInterval? interval, SubscriptionLapseReason? reason)
    {
        switch (source)
        {
            case Enums.SubscriptionSource.Stripe:
                StripeCoverageExpiresAtUtc = expiresAtUtc;
                StripeCoverageInterval = interval;
                StripeCoverageLapseReason = reason;
                break;
            case Enums.SubscriptionSource.GooglePlay:
                PlayCoverageExpiresAtUtc = expiresAtUtc;
                PlayCoverageInterval = interval;
                PlayCoverageLapseReason = reason;
                break;
            case Enums.SubscriptionSource.AppStore:
                AppStoreCoverageExpiresAtUtc = expiresAtUtc;
                AppStoreCoverageInterval = interval;
                AppStoreCoverageLapseReason = reason;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    private void SelectPaidCoverage(SubscriptionSource updatedSource, SubscriptionLapseReason? endedReason = null)
    {
        var candidates = new[]
        {
            (Source: Enums.SubscriptionSource.Stripe, ExpiresAtUtc: StripeCoverageExpiresAtUtc, Interval: StripeCoverageInterval, Reason: StripeCoverageLapseReason),
            (Source: Enums.SubscriptionSource.GooglePlay, ExpiresAtUtc: PlayCoverageExpiresAtUtc, Interval: PlayCoverageInterval, Reason: PlayCoverageLapseReason),
            (Source: Enums.SubscriptionSource.AppStore, ExpiresAtUtc: AppStoreCoverageExpiresAtUtc, Interval: AppStoreCoverageInterval, Reason: AppStoreCoverageLapseReason)
        };
        var nowAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var selected = candidates.Where(candidate => candidate.ExpiresAtUtc > nowAtUtc)
            .OrderByDescending(candidate => candidate.ExpiresAtUtc)
            .ThenByDescending(candidate => candidate.Source == SubscriptionSource)
            .FirstOrDefault();
        if (selected.ExpiresAtUtc is null)
        {
            if (endedReason.HasValue)
            {
                ClearSubscriptionEntitlement(endedReason.Value);
                return;
            }
            selected = candidates.Single(candidate => candidate.Source == updatedSource);
        }
        SubscriptionSource = selected.Source;
        SubscriptionPlan = UserPlan.Pro;
        SubscriptionExpiresAtUtc = selected.ExpiresAtUtc;
        SubscriptionInterval = selected.Interval;
        SubscriptionLapseReason = selected.Reason;
        SubscriptionEndedAtUtc = null;
    }
}
