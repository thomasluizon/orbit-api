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
        var snapshot = Snapshot(ApiMemoryBudget.WarningBytes - 1);
        monitor.Observe(snapshot, nowAtUtc);
        logger.Logs.Should().BeEmpty();
        var high = Snapshot(ApiMemoryBudget.WarningBytes);
        monitor.Observe(high, nowAtUtc);
        monitor.Observe(high, nowAtUtc.AddSeconds(30));
        monitor.Observe(high, nowAtUtc.AddMinutes(5));
        logger.Logs.Should().HaveCount(2).And.OnlyContain(log => log.Level == LogLevel.Warning);
        logger.Logs[0].Message.Should().Contain("314572800").And.Contain("536870912").And.Contain("268435456");
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
        var high = Snapshot(ApiMemoryBudget.WarningBytes);

        monitor.Observe(high, nowAtUtc);
        monitor.Observe(Snapshot(1), nowAtUtc.AddSeconds(30));

        logger.Logs.Should().BeEmpty();
    }

    [Fact]
    public void Observe_DecidesOnAnonymousPlusShmemMemoryAndIgnoresReclaimableFilePages()
    {
        var logger = new CaptureLogger();
        var monitor = new ProcessMemoryMonitor(logger, TimeProvider.System);
        var nowAtUtc = DateTimeOffset.UnixEpoch;
        var half = ApiMemoryBudget.WarningBytes / 2;

        monitor.Observe(new ProcessMemorySnapshot(ApiMemoryBudget.ContainerBytes, half, half - 1, 100, 200, ApiMemoryBudget.HeapBytes), nowAtUtc);
        logger.Logs.Should().BeEmpty("file pages fill the working set, but the kernel reclaims them before it kills the container");

        monitor.Observe(new ProcessMemorySnapshot(ApiMemoryBudget.WarningBytes, half, ApiMemoryBudget.WarningBytes - half, 100, 200, ApiMemoryBudget.HeapBytes),
            nowAtUtc.AddSeconds(30));
        logger.Logs.Should().ContainSingle().Which.Should().Be((LogLevel.Warning,
            $"API memory budget exceeded: anonymous plus shmem {ApiMemoryBudget.WarningBytes} bytes ({ApiMemoryBudget.WarningBytes * 100d / ApiMemoryBudget.ContainerBytes:F1}% of {ApiMemoryBudget.ContainerBytes}); anonymous {half} bytes; shmem {ApiMemoryBudget.WarningBytes - half} bytes; working set {ApiMemoryBudget.WarningBytes} bytes; managed 100 bytes; committed heap 200 bytes; heap limit {ApiMemoryBudget.HeapBytes} bytes"));
    }

    [Fact]
    public void ParseStatus_ReadsAnonymousAndShmemKilobytesFromTheKernelFormat()
    {
        string[] status = ["Name:\tdotnet", "VmRSS:\t  271360 kB", "RssAnon:\t   72120 kB", "RssFile:\t  152168 kB", "RssShmem:\t   47072 kB", "Threads:\t41"];

        ProcessMemorySnapshot.ParseStatus(status).Should().Be((72120L * 1024, 47072L * 1024));
    }

    [Theory]
    [InlineData("RssShmem:\t0 kB")]
    [InlineData("RssAnon:\t0 kB")]
    [InlineData("RssAnon:\t12 MB", "RssShmem:\t0 kB")]
    public void ParseStatus_RejectsAStatusWithoutBothKilobyteLines(params string[] status)
    {
        var parse = () => ProcessMemorySnapshot.ParseStatus(status);

        parse.Should().Throw<FormatException>();
    }

    [Fact]
    public void Snapshot_ReadsTheCurrentProcessWithoutCollectingTheHeap()
    {
        var snapshot = ProcessMemorySnapshot.Read();
        snapshot.WorkingSetBytes.Should().BePositive();
        snapshot.AnonymousBytes.Should().BePositive();
        snapshot.ShmemBytes.Should().BeGreaterThanOrEqualTo(0);
        snapshot.UnreclaimableBytes.Should().Be(snapshot.AnonymousBytes + snapshot.ShmemBytes);
        snapshot.ManagedBytes.Should().BePositive();
        snapshot.HeapLimitBytes.Should().BePositive();
        if (OperatingSystem.IsLinux())
            snapshot.UnreclaimableBytes.Should().BeLessThanOrEqualTo(snapshot.WorkingSetBytes);
    }

    private static ProcessMemorySnapshot Snapshot(long unreclaimableBytes) =>
        new(unreclaimableBytes, unreclaimableBytes, 0, 100, 200, ApiMemoryBudget.HeapBytes);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_LogsStartupObservesEveryThirtySecondsAndStopsOnCancellation(bool loggingEnabled)
    {
        var logger = new CaptureLogger(loggingEnabled);
        var time = new PeriodicTestClock();
        var sampledAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var high = Snapshot(ApiMemoryBudget.WarningBytes);
        var snapshots = new ConcurrentQueue<ProcessMemorySnapshot>([high, Snapshot(1)]);
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
                    $"API memory budget {ApiMemoryBudget.ContainerBytes} bytes; anonymous plus shmem warning {ApiMemoryBudget.WarningBytes} bytes; heap limit {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes} bytes; server GC {GCSettings.IsServerGC}"));
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
                $"API memory anonymous plus shmem recovered below budget: 1 bytes; warning {ApiMemoryBudget.WarningBytes} bytes"));
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
