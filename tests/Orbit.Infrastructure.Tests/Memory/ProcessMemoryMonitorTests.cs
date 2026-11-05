using System.Collections.Concurrent;
using System.Runtime;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Observability;

namespace Orbit.Infrastructure.Tests.Memory;

public class ProcessMemoryMonitorTests
{
    [Fact]
    public void Observe_WarnsAtTheBudgetRepeatsAfterFiveMinutesAndReportsRecovery()
    {
        var logger = new CaptureLogger();
        var monitor = new ProcessMemoryMonitor(logger, TimeProvider.System);
        var nowAtUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new ProcessMemorySnapshot(ApiMemoryBudget.WarningBytes - 1, 100, 200, ApiMemoryBudget.HeapBytes);
        monitor.Observe(snapshot, nowAtUtc);
        logger.Logs.Should().BeEmpty();
        var high = snapshot with { WorkingSetBytes = ApiMemoryBudget.WarningBytes };
        monitor.Observe(high, nowAtUtc);
        monitor.Observe(high, nowAtUtc.AddSeconds(30));
        monitor.Observe(high, nowAtUtc.AddMinutes(5));
        logger.Logs.Should().HaveCount(2).And.OnlyContain(log => log.Level == LogLevel.Warning);
        logger.Logs[0].Message.Should().Contain("429496729").And.Contain("536870912").And.Contain("268435456");
        monitor.Observe(snapshot, nowAtUtc.AddMinutes(6));
        monitor.Observe(snapshot, nowAtUtc.AddMinutes(7));
        logger.Logs.Should().HaveCount(3);
        logger.Logs[2].Level.Should().Be(LogLevel.Information);
        monitor.Observe(high, nowAtUtc.AddMinutes(7));
        logger.Logs.Should().HaveCount(4);
    }

    [Fact]
    public void Observe_DoesNotWriteDisabledLogs()
    {
        var logger = new CaptureLogger(enabled: false);
        using var monitor = new ProcessMemoryMonitor(logger, TimeProvider.System);
        var nowAtUtc = DateTimeOffset.UnixEpoch;
        var high = new ProcessMemorySnapshot(ApiMemoryBudget.WarningBytes, 100, 200, ApiMemoryBudget.HeapBytes);

        monitor.Observe(high, nowAtUtc);
        monitor.Observe(high with { WorkingSetBytes = 1 }, nowAtUtc.AddSeconds(30));

        logger.Logs.Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_ReadsTheCurrentProcessWithoutCollectingTheHeap()
    {
        var snapshot = ProcessMemorySnapshot.Read();
        snapshot.WorkingSetBytes.Should().BePositive();
        snapshot.ManagedBytes.Should().BePositive();
        snapshot.HeapLimitBytes.Should().BePositive();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_LogsStartupObservesEveryThirtySecondsAndStopsOnCancellation(bool loggingEnabled)
    {
        var logger = new CaptureLogger(loggingEnabled);
        var time = new PeriodicTestClock();
        var sampledAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var high = new ProcessMemorySnapshot(ApiMemoryBudget.WarningBytes, 100, 200, ApiMemoryBudget.HeapBytes);
        var snapshots = new ConcurrentQueue<ProcessMemorySnapshot>([high, high with { WorkingSetBytes = 1 }]);
        using var monitor = new ProcessMemoryMonitor(logger, time, () =>
        {
            if (!snapshots.TryDequeue(out var snapshot))
                throw new InvalidOperationException("Memory sampling continued after the expected observations.");
            if (snapshots.IsEmpty)
                sampledAgain.TrySetResult();
            return snapshot;
        });

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10));
            snapshots.Should().HaveCount(1);
            time.DueTime.Should().Be(TimeSpan.FromSeconds(30));
            time.Period.Should().Be(TimeSpan.FromSeconds(30));
            if (loggingEnabled)
            {
                logger.Logs.Should().HaveCount(3);
                logger.Logs[0].Should().Be((LogLevel.Information,
                    $"API memory budget {ApiMemoryBudget.ContainerBytes} bytes; warning {ApiMemoryBudget.WarningBytes} bytes; heap limit {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes} bytes; server GC {GCSettings.IsServerGC}"));
                logger.Logs[1].Should().Be((LogLevel.Information,
                    $"API GC configuration {JsonSerializer.Serialize(GC.GetConfigurationVariables())}"));
                logger.Logs[2].Level.Should().Be(LogLevel.Warning);
                logger.Logs[2].Message.Should().Contain("API memory budget exceeded");
            }

            time.Advance(TimeSpan.FromSeconds(29));
            snapshots.Should().HaveCount(1);
            logger.Logs.Should().HaveCount(loggingEnabled ? 3 : 0);

            time.Advance(TimeSpan.FromSeconds(1));
            await sampledAgain.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        monitor.ExecuteTask.Should().NotBeNull();
        monitor.ExecuteTask!.IsCompleted.Should().BeTrue();
        monitor.ExecuteTask.IsFaulted.Should().BeFalse();
        time.TimerDisposed.Should().BeTrue();
        snapshots.Should().BeEmpty();
        if (loggingEnabled)
        {
            logger.Logs.Should().HaveCount(4);
            logger.Logs[3].Should().Be((LogLevel.Information,
                $"API memory working set recovered below budget: 1 bytes; warning {ApiMemoryBudget.WarningBytes} bytes"));
        }
        else
            logger.Logs.Should().BeEmpty();

        time.Advance(TimeSpan.FromSeconds(30));
        logger.Logs.Should().HaveCount(loggingEnabled ? 4 : 0);
    }

    private sealed class PeriodicTestClock : TimeProvider
    {
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset _nowAtUtc = DateTimeOffset.UnixEpoch;
        private DateTimeOffset _nextTickAtUtc;
        private TimerCallback? _callback;
        private object? _state;

        internal Task TimerCreated => _timerCreated.Task;
        internal TimeSpan DueTime { get; private set; }
        internal TimeSpan Period { get; private set; }
        internal bool TimerDisposed { get; private set; }

        public override DateTimeOffset GetUtcNow() => _nowAtUtc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            Period = period;
            _nextTickAtUtc = _nowAtUtc + dueTime;
            var timer = Substitute.For<ITimer>();
            timer.When(value => value.Dispose()).Do(_ => TimerDisposed = true);
            _timerCreated.TrySetResult();
            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            _nowAtUtc += elapsed;
            while (!TimerDisposed && _callback is not null && _nextTickAtUtc <= _nowAtUtc)
            {
                _nextTickAtUtc += Period;
                _callback(_state);
            }
        }
    }

    private sealed class CaptureLogger(bool enabled = true) : ILogger<ProcessMemoryMonitor>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _logs = new();
        internal (LogLevel Level, string Message)[] Logs => _logs.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => enabled;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _logs.Enqueue((logLevel, formatter(state, exception)));
    }
}
