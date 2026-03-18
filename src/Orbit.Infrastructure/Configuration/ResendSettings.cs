namespace Orbit.Infrastructure.Configuration;

public sealed class ResendSettings
{
    public const string SectionName = "Resend";
    public required string ApiKey { get; init; }
    public required string FromEmail { get; init; }
    public string SupportEmail { get; init; } = "support@useorbit.org";
}
