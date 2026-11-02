namespace Orbit.Infrastructure.Persistence;

public sealed class BackgroundJobHeartbeat
{
    public string JobName { get; private set; } = string.Empty;
    public DateTime RegisteredAtUtc { get; private set; }
    public DateTime? LastSuccessfulTickAtUtc { get; private set; }
}
