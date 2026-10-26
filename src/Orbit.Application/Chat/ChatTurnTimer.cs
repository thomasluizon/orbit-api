using Orbit.Application.Chat.Models;

namespace Orbit.Application.Chat;

public enum ChatPhase
{
    Moderation, ContextLoad, PromptBuild, InputTokenCount, ModelCall, ToolCall, Save, Composition, Budget
}

/// <summary>
/// Measures exclusive wall time. Nested phases replace their parent while active; concurrent
/// leaf phases share overlapping time equally. ElapsedMilliseconds retains each scope's full duration.
/// </summary>
public sealed class ChatTurnTimer : IDisposable
{
    private static readonly AsyncLocal<ChatTurnTimer?> Current = new();
    private static readonly AsyncLocal<PhaseScope?> CurrentPhase = new();
    private readonly ChatTurnTimer? _previous;
    private readonly PhaseScope? _previousPhase;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly List<PhaseScope> _phases = [];
    private readonly HashSet<PhaseScope> _active = [];
    private readonly object _gate = new();
    private long _lastTimestamp;
    private double _otherMilliseconds;
    private ChatTurnTiming? _completed;

    private ChatTurnTimer(TimeProvider clock)
    {
        _clock = clock;
        _started = _lastTimestamp = clock.GetTimestamp();
        _previous = Current.Value;
        _previousPhase = CurrentPhase.Value;
        Current.Value = this;
    }

    public static ChatTurnTimer Begin(TimeProvider clock) => new(clock);

    public static IDisposable? Measure(ChatPhase phase)
    {
        var timer = Current.Value;
        if (timer is null)
            return null;
        var parent = CurrentPhase.Value;
        if (parent?.Owner != timer)
            parent = null;
        if (parent?.Phase == phase)
            return null;
        lock (timer._gate)
        {
            if (timer._completed is not null)
                return null;
            var timestamp = timer._clock.GetTimestamp();
            timer.Accrue(timestamp);
            var scope = new PhaseScope(timer, phase, parent, timestamp);
            timer._phases.Add(scope);
            timer._active.Add(scope);
            CurrentPhase.Value = scope;
            return scope;
        }
    }

    public ChatTurnTiming Complete()
    {
        lock (_gate)
        {
            if (_completed is not null)
                return _completed;
            var timestamp = _clock.GetTimestamp();
            Accrue(timestamp);
            var phases = _phases.Select(scope => new ChatPhaseTiming(Name(scope.Phase), scope.Milliseconds,
                MillisecondsBetween(scope.Started, scope.Ended ?? timestamp))).ToList();
            phases.Add(new ChatPhaseTiming("other", _otherMilliseconds, _otherMilliseconds));
            _completed = new ChatTurnTiming(MillisecondsBetween(_started, timestamp), phases);
            return _completed;
        }
    }

    private void Accrue(long timestamp)
    {
        var elapsed = MillisecondsBetween(_lastTimestamp, timestamp);
        var parents = _active.Select(scope => scope.Parent).ToHashSet();
        var leaves = _active.Where(scope => !parents.Contains(scope)).ToArray();
        if (leaves.Length == 0)
            _otherMilliseconds += elapsed;
        else
            foreach (var scope in leaves)
                scope.Milliseconds += elapsed / leaves.Length;
        _lastTimestamp = timestamp;
    }

    private double MillisecondsBetween(long start, long end) => (end - start) * 1000d / _clock.TimestampFrequency;

    private static string Name(ChatPhase phase) => phase switch
    {
        ChatPhase.Moderation => "moderation",
        ChatPhase.ContextLoad => "context_load",
        ChatPhase.PromptBuild => "prompt_build",
        ChatPhase.InputTokenCount => "input_token_count",
        ChatPhase.ModelCall => "model_call",
        ChatPhase.ToolCall => "tool_call",
        ChatPhase.Save => "save",
        ChatPhase.Composition => "composition",
        ChatPhase.Budget => "budget",
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    public void Dispose()
    {
        Complete();
        Current.Value = _previous;
        CurrentPhase.Value = _previousPhase;
    }

    private sealed class PhaseScope(ChatTurnTimer owner, ChatPhase phase, PhaseScope? parent, long started) : IDisposable
    {
        public ChatTurnTimer Owner { get; } = owner;
        public ChatPhase Phase { get; } = phase;
        public PhaseScope? Parent { get; } = parent;
        public long Started { get; } = started;
        public long? Ended { get; private set; }
        public double Milliseconds { get; set; }

        public void Dispose()
        {
            lock (Owner._gate)
            {
                if (Ended is not null)
                    return;
                var timestamp = Owner._clock.GetTimestamp();
                if (Owner._completed is null)
                    Owner.Accrue(timestamp);
                Ended = timestamp;
                Owner._active.Remove(this);
                CurrentPhase.Value = Parent;
            }
        }
    }
}
