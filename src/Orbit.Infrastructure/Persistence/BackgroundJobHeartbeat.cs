namespace Orbit.Infrastructure.Persistence;

public sealed class BackgroundJobHeartbeat
{
    public string JobName { get; init; } = string.Empty;
    public DateTime RegisteredAtUtc { get; init; }
    public DateTime? LastSuccessfulTickAtUtc { get; init; }
}
