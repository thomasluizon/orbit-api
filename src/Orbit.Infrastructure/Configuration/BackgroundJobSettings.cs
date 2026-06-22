namespace Orbit.Infrastructure.Configuration;

/// <summary>
/// Background-processing rollout settings. When <see cref="UseDurableQueue"/> is false (default),
/// each recurring scheduler runs as its own in-process <c>BackgroundService</c> polling loop exactly
/// as before. When true, those recurring scans are registered as Hangfire recurring jobs backed by
/// PostgreSQL instead: occurrences survive restarts, a distributed lock prevents more than one
/// instance running the same occurrence, and failed runs retry with exponential backoff. The
/// one-shot startup data-encryption migration always runs as a hosted service regardless of this flag.
/// </summary>
public sealed class BackgroundJobSettings
{
    public const string SectionName = "BackgroundServices";

    public bool UseDurableQueue { get; init; }
}
