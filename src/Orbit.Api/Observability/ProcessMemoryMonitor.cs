using System.Diagnostics;
using System.Runtime;
using System.Text.Json;

namespace Orbit.Api.Observability;

internal sealed record ProcessMemorySnapshot(long WorkingSetBytes, long ManagedBytes, long CommittedBytes, long HeapLimitBytes)
{
    internal static ProcessMemorySnapshot Read()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new(process.WorkingSet64, GC.GetTotalMemory(false), gc.TotalCommittedBytes, gc.TotalAvailableMemoryBytes);
    }
}

internal sealed partial class ProcessMemoryMonitor(ILogger<ProcessMemoryMonitor> logger, TimeProvider time) : BackgroundService
{
    private readonly Func<ProcessMemorySnapshot> _readSnapshot = ProcessMemorySnapshot.Read;
    private bool _aboveBudget;
    private DateTimeOffset _lastWarningAtUtc;

    internal ProcessMemoryMonitor(ILogger<ProcessMemoryMonitor> logger, TimeProvider time,
        Func<ProcessMemorySnapshot> readSnapshot) : this(logger, time) => _readSnapshot = readSnapshot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            LogBudget(logger, ApiMemoryBudget.ContainerBytes, ApiMemoryBudget.WarningBytes,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, GCSettings.IsServerGC);
            LogConfiguration(logger, JsonSerializer.Serialize(GC.GetConfigurationVariables()));
        }
        Observe(_readSnapshot(), time.GetUtcNow());
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            Observe(_readSnapshot(), time.GetUtcNow());
    }

    internal void Observe(ProcessMemorySnapshot snapshot, DateTimeOffset sampledAtUtc)
    {
        if (snapshot.WorkingSetBytes >= ApiMemoryBudget.WarningBytes)
        {
            if (!_aboveBudget || sampledAtUtc - _lastWarningAtUtc >= TimeSpan.FromMinutes(5))
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    LogBudgetExceeded(logger, snapshot.WorkingSetBytes, snapshot.WorkingSetBytes * 100d / ApiMemoryBudget.ContainerBytes,
                        ApiMemoryBudget.ContainerBytes, snapshot.ManagedBytes, snapshot.CommittedBytes, snapshot.HeapLimitBytes);
                _lastWarningAtUtc = sampledAtUtc;
            }
            _aboveBudget = true;
        }
        else if (_aboveBudget)
        {
            LogRecovery(logger, snapshot.WorkingSetBytes, ApiMemoryBudget.WarningBytes);
            _aboveBudget = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "API memory budget {ContainerLimitBytes} bytes; warning {WarningBytes} bytes; heap limit {HeapLimitBytes} bytes; server GC {ServerGC}")]
    private static partial void LogBudget(ILogger logger, long containerLimitBytes, long warningBytes, long heapLimitBytes, bool serverGC);

    [LoggerMessage(Level = LogLevel.Information, Message = "API GC configuration {GCConfiguration}")]
    private static partial void LogConfiguration(ILogger logger, string gcConfiguration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "API memory budget exceeded: working set {WorkingSetBytes} bytes ({WorkingSetPercent:F1}% of {ContainerLimitBytes}); managed {ManagedBytes} bytes; committed heap {CommittedBytes} bytes; heap limit {HeapLimitBytes} bytes")]
    private static partial void LogBudgetExceeded(ILogger logger, long workingSetBytes, double workingSetPercent,
        long containerLimitBytes, long managedBytes, long committedBytes, long heapLimitBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "API memory working set recovered below budget: {WorkingSetBytes} bytes; warning {WarningBytes} bytes")]
    private static partial void LogRecovery(ILogger logger, long workingSetBytes, long warningBytes);
}
