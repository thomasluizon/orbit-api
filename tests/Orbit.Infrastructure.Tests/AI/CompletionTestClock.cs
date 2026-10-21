namespace Orbit.Infrastructure.Tests.AI;

internal sealed class CompletionTestClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    internal int ActiveTimers => _timers.Count(timer => !timer.Disposed);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    internal void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        _ticks += elapsed.Ticks;
        foreach (var timer in _timers.ToArray())
            timer.FireIfDue();
    }

    private sealed class ManualTimer(CompletionTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long _dueTicks = long.MaxValue;
        internal bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Disposed)
                return false;
            if (period != Timeout.InfiniteTimeSpan)
                throw new NotSupportedException("Completion deadlines require one-shot timers.");
            _dueTicks = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
            return true;
        }

        internal void FireIfDue()
        {
            if (Disposed || _dueTicks > clock._ticks)
                return;
            _dueTicks = long.MaxValue;
            callback(state);
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
