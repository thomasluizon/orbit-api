namespace Orbit.Infrastructure.Configuration;

public sealed class ResendSettings
{
    public const string SectionName = "Resend";
    public required string ApiKey { get; init; }
    public required string FromEmail { get; init; }
    public string SupportEmail { get; init; } = "contact@useorbit.org";
    public string MarketingFromEmail { get; init; } = "Orbit <news@updates.useorbit.org>";
}
