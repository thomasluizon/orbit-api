using Orbit.Domain.Enums;

namespace Orbit.Application.Common;

public class GooglePlaySettings
{
    public const string SectionName = "GooglePlay";
    public string PackageName { get; set; } = "";
    public string ServiceAccountJson { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string MonthlyBasePlanId { get; set; } = "";
    public string YearlyBasePlanId { get; set; } = "";
    public string RtdnAudience { get; set; } = "";
    public string RtdnServiceAccountEmail { get; set; } = "";

    /// <summary>
    /// Throws if any value required to verify Play purchases or authenticate RTDN pushes is
    /// missing. A blank value would let purchases silently fail or accept unauthenticated
    /// push notifications, so the API must refuse to boot. Call once at startup outside Development.
    /// </summary>
    public void Validate()
    {
        var missingKeys = new List<string>();
        if (string.IsNullOrWhiteSpace(PackageName)) missingKeys.Add($"{SectionName}:{nameof(PackageName)}");
        if (string.IsNullOrWhiteSpace(ServiceAccountJson)) missingKeys.Add($"{SectionName}:{nameof(ServiceAccountJson)}");
        if (string.IsNullOrWhiteSpace(ProductId)) missingKeys.Add($"{SectionName}:{nameof(ProductId)}");
        if (string.IsNullOrWhiteSpace(MonthlyBasePlanId)) missingKeys.Add($"{SectionName}:{nameof(MonthlyBasePlanId)}");
        if (string.IsNullOrWhiteSpace(YearlyBasePlanId)) missingKeys.Add($"{SectionName}:{nameof(YearlyBasePlanId)}");
        if (string.IsNullOrWhiteSpace(RtdnAudience)) missingKeys.Add($"{SectionName}:{nameof(RtdnAudience)}");
        if (string.IsNullOrWhiteSpace(RtdnServiceAccountEmail)) missingKeys.Add($"{SectionName}:{nameof(RtdnServiceAccountEmail)}");

        if (missingKeys.Count > 0)
            throw new InvalidOperationException(
                $"Missing required Google Play setting(s): {string.Join(", ", missingKeys)}. " +
                "Set all GooglePlay__* values before starting the API.");
    }

    /// <summary>Maps a verified Play base-plan id back to the subscription interval, or null if unrecognized.</summary>
    public SubscriptionInterval? IntervalForBasePlan(string? basePlanId)
    {
        if (string.IsNullOrWhiteSpace(basePlanId)) return null;
        if (string.Equals(basePlanId, MonthlyBasePlanId, StringComparison.OrdinalIgnoreCase))
            return SubscriptionInterval.Monthly;
        if (string.Equals(basePlanId, YearlyBasePlanId, StringComparison.OrdinalIgnoreCase))
            return SubscriptionInterval.Yearly;
        return null;
    }
}
