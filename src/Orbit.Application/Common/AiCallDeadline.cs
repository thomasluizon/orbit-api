namespace Orbit.Application.Common;

/// <summary>
/// Declares a token that its owner cancels only when a deadline expires, such as the chat turn budget or the
/// shutdown drain. An AI completion scope under this binding reads an expiry of the token as a failure, not as a
/// caller cancellation. The binding flows through the async context, and an enclosing binding stays in force.
/// </summary>
public sealed class AiCallDeadline : IDisposable
{
    private static readonly AsyncLocal<AiCallDeadline?> Active = new();
    private readonly AiCallDeadline? _previous;
    private readonly CancellationToken _deadline;

    private AiCallDeadline(CancellationToken deadline)
    {
        _deadline = deadline;
        _previous = Active.Value;
        Active.Value = this;
    }

    public static AiCallDeadline? Current => Active.Value;

    public static AiCallDeadline Bind(CancellationToken deadline) => new(deadline);

    /// <summary>True when this deadline or an enclosing one expired.</summary>
    public bool Expired => _deadline.IsCancellationRequested || _previous?.Expired == true;

    public void Dispose()
    {
        if (Active.Value == this)
            Active.Value = _previous;
    }
}
