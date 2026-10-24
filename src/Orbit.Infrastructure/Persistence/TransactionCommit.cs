namespace Orbit.Infrastructure.Persistence;

internal sealed class TransactionCommit
{
    public Guid Id { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
}
