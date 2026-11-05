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

internal sealed class ProcessMemoryMonitor(ILogger<ProcessMemoryMonitor> logger, TimeProvider time) : BackgroundService
{
    private bool _aboveBudget;
    private DateTimeOffset _lastWarningAtUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("API memory budget {ContainerLimitBytes} bytes; warning {WarningBytes} bytes; heap limit {HeapLimitBytes} bytes; server GC {ServerGC}",
            ApiMemoryBudget.ContainerBytes, ApiMemoryBudget.WarningBytes, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, GCSettings.IsServerGC);
        logger.LogInformation("API GC configuration {GCConfiguration}", JsonSerializer.Serialize(GC.GetConfigurationVariables()));
        Observe(ProcessMemorySnapshot.Read(), time.GetUtcNow());
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            Observe(ProcessMemorySnapshot.Read(), time.GetUtcNow());
    }

    internal void Observe(ProcessMemorySnapshot snapshot, DateTimeOffset sampledAtUtc)
    {
        if (snapshot.WorkingSetBytes >= ApiMemoryBudget.WarningBytes)
        {
            if (!_aboveBudget || sampledAtUtc - _lastWarningAtUtc >= TimeSpan.FromMinutes(5))
            {
                logger.LogWarning("API memory budget exceeded: working set {WorkingSetBytes} bytes ({WorkingSetPercent:F1}% of {ContainerLimitBytes}); managed {ManagedBytes} bytes; committed heap {CommittedBytes} bytes; heap limit {HeapLimitBytes} bytes",
                    snapshot.WorkingSetBytes, snapshot.WorkingSetBytes * 100d / ApiMemoryBudget.ContainerBytes,
                    ApiMemoryBudget.ContainerBytes, snapshot.ManagedBytes, snapshot.CommittedBytes, snapshot.HeapLimitBytes);
                _lastWarningAtUtc = sampledAtUtc;
            }
            _aboveBudget = true;
        }
        else if (_aboveBudget)
        {
            logger.LogInformation("API memory working set recovered below budget: {WorkingSetBytes} bytes; warning {WarningBytes} bytes",
                snapshot.WorkingSetBytes, ApiMemoryBudget.WarningBytes);
            _aboveBudget = false;
        }
    }
}
