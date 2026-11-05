using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    public void Snapshot_ReadsTheCurrentProcessWithoutCollectingTheHeap()
    {
        var snapshot = ProcessMemorySnapshot.Read();
        snapshot.WorkingSetBytes.Should().BePositive();
        snapshot.ManagedBytes.Should().BePositive();
        snapshot.HeapLimitBytes.Should().BePositive();
    }

    private sealed class CaptureLogger : ILogger<ProcessMemoryMonitor>
    {
        internal List<(LogLevel Level, string Message)> Logs { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Logs.Add((logLevel, formatter(state, exception)));
    }
}
