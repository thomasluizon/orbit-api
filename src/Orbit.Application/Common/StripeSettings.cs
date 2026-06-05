namespace Orbit.Application.Common;

public class StripeSettings
{
    public const string SectionName = "Stripe";
    public string SecretKey { get; set; } = "";
    public string PublishableKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string MonthlyPriceIdUsd { get; set; } = "";
    public string YearlyPriceIdUsd { get; set; } = "";
    public string MonthlyPriceIdBrl { get; set; } = "";
    public string YearlyPriceIdBrl { get; set; } = "";
    public string SuccessUrl { get; set; } = "";
    public string CancelUrl { get; set; } = "";
    public string ProProductId { get; set; } = "";

    /// <summary>
    /// Throws if any of the four checkout price IDs (BRL/USD × monthly/yearly) is missing.
    /// A blank ID would silently degrade affected users to the wrong currency, so the API
    /// must refuse to boot. Call once at startup.
    /// </summary>
    public void ValidatePriceIds()
    {
        var missingKeys = new List<string>();
        if (string.IsNullOrWhiteSpace(MonthlyPriceIdUsd)) missingKeys.Add($"{SectionName}:{nameof(MonthlyPriceIdUsd)}");
        if (string.IsNullOrWhiteSpace(YearlyPriceIdUsd)) missingKeys.Add($"{SectionName}:{nameof(YearlyPriceIdUsd)}");
        if (string.IsNullOrWhiteSpace(MonthlyPriceIdBrl)) missingKeys.Add($"{SectionName}:{nameof(MonthlyPriceIdBrl)}");
        if (string.IsNullOrWhiteSpace(YearlyPriceIdBrl)) missingKeys.Add($"{SectionName}:{nameof(YearlyPriceIdBrl)}");

        if (missingKeys.Count > 0)
            throw new InvalidOperationException(
                $"Missing required Stripe price ID(s): {string.Join(", ", missingKeys)}. " +
                "Set all four BRL/USD monthly/yearly price IDs before starting the API.");
    }
}
