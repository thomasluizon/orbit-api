namespace Orbit.Infrastructure.AI;

public sealed class AiCallContext : IDisposable
{
    private static readonly AsyncLocal<AiCallContext?> Active = new();
    private readonly AiCallContext? _previous;
    private int _unreportedDispatches;
    public Guid UserId { get; }
    public long? ChargedMicroDollars { get; private set; }
    public static AiCallContext? Current => Active.Value;
    internal bool HasUnreportedDispatch => _unreportedDispatches > 0;

    private AiCallContext(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("An AI call requires a user.", nameof(userId));
        UserId = userId;
        _previous = Active.Value;
        Active.Value = this;
    }

    public static AiCallContext Begin(Guid userId) => new(userId);
    internal void MarkDispatched() => _unreportedDispatches++;
    internal void MarkRejected() => _unreportedDispatches--;
    public void ReportCost(long microDollars)
    {
        if (microDollars < 0)
            throw new ArgumentOutOfRangeException(nameof(microDollars));
        ChargedMicroDollars = checked((ChargedMicroDollars ?? 0) + microDollars);
    }
    public void Dispose() => Active.Value = _previous;
}
