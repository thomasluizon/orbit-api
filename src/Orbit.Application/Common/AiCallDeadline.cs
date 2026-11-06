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

    /// <summary>
    /// Starts a budget of <paramref name="timeout"/> on <paramref name="clock"/>, links it to the caller's token
    /// and binds only the budget timer. An AI call on <see cref="Budget.Token"/> then reads the budget expiry as a
    /// failure, and a cancellation of <paramref name="caller"/> stays a caller cancellation.
    /// </summary>
    public static Budget Start(TimeSpan timeout, TimeProvider clock, CancellationToken caller) => new(timeout, clock, caller);

    /// <summary>True when this deadline or an enclosing one expired.</summary>
    public bool Expired => _deadline.IsCancellationRequested || _previous?.Expired == true;

    public void Dispose()
    {
        if (Active.Value == this)
            Active.Value = _previous;
    }

    /// <summary>A caller-linked budget whose timer is bound as an <see cref="AiCallDeadline"/>.</summary>
    public sealed class Budget : IDisposable
    {
        private readonly CancellationTokenSource _timer;
        private readonly CancellationTokenSource _linked;
        private readonly AiCallDeadline _binding;

        internal Budget(TimeSpan timeout, TimeProvider clock, CancellationToken caller)
        {
            _timer = new CancellationTokenSource(timeout, clock);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _timer.Token);
            _binding = Bind(_timer.Token);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            _binding.Dispose();
            _linked.Dispose();
            _timer.Dispose();
        }
    }
}
