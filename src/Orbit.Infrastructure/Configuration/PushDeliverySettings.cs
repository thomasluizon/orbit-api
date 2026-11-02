namespace Orbit.Infrastructure.Configuration;

public sealed class PushDeliverySettings
{
    public const string SectionName = "PushDelivery";
    public int Concurrency { get; set; } = 8;
    public int AttemptTimeoutSeconds { get; set; } = 10;
}
