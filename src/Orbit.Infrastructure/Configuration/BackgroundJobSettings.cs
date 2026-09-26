namespace Orbit.Infrastructure.Configuration;

public sealed class BackgroundJobSettings
{
    public const string SectionName = "BackgroundServices";

    public bool UseDurableQueue { get; init; }
}
