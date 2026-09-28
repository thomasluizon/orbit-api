namespace Orbit.Infrastructure.Configuration;

public sealed class SesSettings
{
    public const string SectionName = "Ses";
    public string Region { get; init; } = "us-east-2";
    public string AccessKeyId { get; init; } = "";
    public string SecretAccessKey { get; init; } = "";
    public string FromEmail { get; init; } = "Orbit <noreply@send.useorbit.org>";
    public string SupportEmail { get; init; } = "contact@useorbit.org";
    public string MarketingFromEmail { get; init; } = "Orbit <news@updates.useorbit.org>";
    public string TransactionalConfigurationSet { get; init; } = "orbit-transactional";
    public string MarketingConfigurationSet { get; init; } = "orbit-marketing";
    public string TopicArn { get; init; } = "";
    public double MarketingRetryBaseDelayMs { get; init; } = 500;
}
