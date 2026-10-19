namespace Orbit.Infrastructure.AI;

public sealed class AiCallContext : IDisposable
{
    private static readonly AsyncLocal<AiCallContext?> Active = new();
    private readonly AiCallContext? _previous;
    public Guid UserId { get; }
    public long? Tokens { get; private set; }
    public static AiCallContext? Current => Active.Value;

    private AiCallContext(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("An AI call requires a user.", nameof(userId));
        UserId = userId;
        _previous = Active.Value;
        Active.Value = this;
    }

    public static AiCallContext Begin(Guid userId) => new(userId);
    public void Report(long tokens) => Tokens = Math.Max(Tokens ?? 0, tokens);
    public void Dispose() => Active.Value = _previous;
}
