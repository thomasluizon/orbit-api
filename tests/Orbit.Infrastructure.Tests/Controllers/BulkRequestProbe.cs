using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Orbit.Infrastructure.Tests.Controllers;

internal sealed class BulkRequestProbe
{
    private readonly AsyncLocal<Frame?> _current = new();
    internal Dictionary<string, double> Milliseconds { get; } = [];

    internal T Wrap<T>(object target, Func<MethodInfo, string?> phase) where T : class
    {
        var proxy = DispatchProxy.Create<T, TimedPort<T>>();
        var port = (TimedPort<T>)(object)proxy;
        port.Target = target;
        port.Probe = this;
        port.Phase = phase;
        return proxy;
    }

    private async Task<T> Measure<T>(string phase, Func<Task<T>> operation)
    {
        var parent = _current.Value;
        var frame = new Frame();
        _current.Value = frame;
        var start = Stopwatch.GetTimestamp();
        try { return await operation(); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Milliseconds[phase] = Milliseconds.GetValueOrDefault(phase) + elapsed - frame.Children;
            if (parent is not null) parent.Children += elapsed;
            _current.Value = parent;
        }
    }

    private sealed class Frame { internal double Children; }

    internal class TimedPort<T> : DispatchProxy where T : class
    {
        internal object Target { get; set; } = default!;
        internal BulkRequestProbe Probe { get; set; } = default!;
        internal Func<MethodInfo, string?> Phase { get; set; } = default!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new ArgumentNullException(nameof(targetMethod));
            object? Call()
            {
                try { return method.Invoke(Target, args); }
                catch (TargetInvocationException error) when (error.InnerException is not null)
                {
                    ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                    throw;
                }
            }
            var phase = Phase(method);
            if (phase is null) return Call();
            if (method.ReturnType == typeof(Task))
                return Probe.Measure(phase, async () => { await (Task)Call()!; return true; });
            return typeof(TimedPort<T>).GetMethod(nameof(MeasureResult), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(method.ReturnType.GenericTypeArguments.Single())
                .Invoke(this, [phase, (Func<object?>)Call]);
        }

        private Task<TResult> MeasureResult<TResult>(string phase, Func<object?> call) =>
            Probe.Measure(phase, () => (Task<TResult>)call()!);
    }
}
