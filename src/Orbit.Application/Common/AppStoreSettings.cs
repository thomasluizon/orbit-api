using Orbit.Domain.Enums;

namespace Orbit.Application.Common;

public sealed class AppStoreSettings
{
    public const string SectionName = "AppStore";
    public string KeyId { get; set; } = "";
    public string IssuerId { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string BundleId { get; set; } = "";
    public long AppAppleId { get; set; }
    public string MonthlyProductId { get; set; } = "";
    public string YearlyProductId { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(IssuerId)
        && !string.IsNullOrWhiteSpace(PrivateKey) && !string.IsNullOrWhiteSpace(BundleId) && AppAppleId > 0
        && !string.IsNullOrWhiteSpace(MonthlyProductId) && !string.IsNullOrWhiteSpace(YearlyProductId)
        && MonthlyProductId != YearlyProductId;

    public SubscriptionInterval? IntervalFor(string productId) => productId == MonthlyProductId && MonthlyProductId.Length > 0
        ? SubscriptionInterval.Monthly : productId == YearlyProductId && YearlyProductId.Length > 0 ? SubscriptionInterval.Yearly : null;
}
