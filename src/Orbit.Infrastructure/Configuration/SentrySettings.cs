namespace Orbit.Infrastructure.Configuration;

public sealed class SentrySettings
{
    public const string SectionName = "Sentry";
    public string Dsn { get; init; } = "";
    public string Environment { get; init; } = "production";
    public double TracesSampleRate { get; init; }
}
