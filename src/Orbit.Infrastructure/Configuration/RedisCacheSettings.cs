namespace Orbit.Infrastructure.Configuration;

/// <summary>
/// Distributed-cache rollout settings. When <see cref="Enabled"/> is false (default), the app
/// registers an in-process <c>IDistributedCache</c> and behaves exactly as before. When true,
/// the same <c>IDistributedCache</c> seam is backed by Redis so cached user-date preferences stay
/// consistent across multiple API instances. <see cref="ConnectionString"/> is required when enabled.
/// </summary>
public sealed class RedisCacheSettings
{
    public const string SectionName = "Redis";

    public bool Enabled { get; init; }

    public string ConnectionString { get; init; } = "";

    public string InstanceName { get; init; } = "orbit:";
}
