namespace Orbit.Infrastructure.Configuration;

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
}
