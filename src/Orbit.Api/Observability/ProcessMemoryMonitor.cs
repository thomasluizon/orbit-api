using System.Diagnostics;
using System.Runtime;
using System.Text.Json;

namespace Orbit.Api.Observability;

/// <summary>
/// Process memory at one sample. <see cref="UnreclaimableBytes"/> is anonymous plus shmem memory: the kernel
/// cannot reclaim it without swap, so it decides an out-of-memory kill. File pages in the working set are reclaimable.
/// Without <c>/proc</c> (local development off Linux) the whole working set counts as anonymous memory.
/// </summary>
internal sealed record ProcessMemorySnapshot(long WorkingSetBytes, long AnonymousBytes, long ShmemBytes,
    long ManagedBytes, long CommittedBytes, long HeapLimitBytes)
{
    private const string StatusPath = "/proc/self/status";

    internal long UnreclaimableBytes => AnonymousBytes + ShmemBytes;

    internal static ProcessMemorySnapshot Read()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var (anonymousBytes, shmemBytes) = File.Exists(StatusPath)
            ? ParseStatus(File.ReadLines(StatusPath))
            : (process.WorkingSet64, 0L);
        return new(process.WorkingSet64, anonymousBytes, shmemBytes, GC.GetTotalMemory(false),
            gc.TotalCommittedBytes, gc.TotalAvailableMemoryBytes);
    }

    /// <summary>
    /// Reads <c>RssAnon</c> and <c>RssShmem</c>, which the kernel reports in kB:
    /// https://docs.kernel.org/filesystems/proc.html
    /// </summary>
    internal static (long AnonymousBytes, long ShmemBytes) ParseStatus(IEnumerable<string> lines)
    {
        long? anonymousBytes = null, shmemBytes = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("RssAnon:", StringComparison.Ordinal))
                anonymousBytes = ParseKilobytes(line);
            else if (line.StartsWith("RssShmem:", StringComparison.Ordinal))
                shmemBytes = ParseKilobytes(line);
        }
        return (anonymousBytes ?? throw new FormatException("The process status has no RssAnon line."),
            shmemBytes ?? throw new FormatException("The process status has no RssShmem line."));
    }

    private static long ParseKilobytes(string line)
    {
        var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[2] != "kB")
            throw new FormatException($"Unexpected process status line '{line}'.");
        return checked(long.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture) * 1024);
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
        if (snapshot.UnreclaimableBytes >= ApiMemoryBudget.WarningBytes)
        {
            if (!_aboveBudget || sampledAtUtc - _lastWarningAtUtc >= TimeSpan.FromMinutes(5))
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    LogBudgetExceeded(logger, snapshot.UnreclaimableBytes,
                        snapshot.UnreclaimableBytes * 100d / ApiMemoryBudget.ContainerBytes, ApiMemoryBudget.ContainerBytes,
                        snapshot.AnonymousBytes, snapshot.ShmemBytes, snapshot.WorkingSetBytes,
                        snapshot.ManagedBytes, snapshot.CommittedBytes, snapshot.HeapLimitBytes);
                _lastWarningAtUtc = sampledAtUtc;
            }
            _aboveBudget = true;
        }
        else if (_aboveBudget)
        {
            LogRecovery(logger, snapshot.UnreclaimableBytes, ApiMemoryBudget.WarningBytes);
            _aboveBudget = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "API memory budget {ContainerLimitBytes} bytes; anonymous plus shmem warning {WarningBytes} bytes; heap limit {HeapLimitBytes} bytes; server GC {ServerGC}")]
    private static partial void LogBudget(ILogger logger, long containerLimitBytes, long warningBytes, long heapLimitBytes, bool serverGC);

    [LoggerMessage(Level = LogLevel.Information, Message = "API GC configuration {GCConfiguration}")]
    private static partial void LogConfiguration(ILogger logger, string gcConfiguration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "API memory budget exceeded: anonymous plus shmem {UnreclaimableBytes} bytes ({UnreclaimablePercent:F1}% of {ContainerLimitBytes}); anonymous {AnonymousBytes} bytes; shmem {ShmemBytes} bytes; working set {WorkingSetBytes} bytes; managed {ManagedBytes} bytes; committed heap {CommittedBytes} bytes; heap limit {HeapLimitBytes} bytes")]
    private static partial void LogBudgetExceeded(ILogger logger, long unreclaimableBytes, double unreclaimablePercent,
        long containerLimitBytes, long anonymousBytes, long shmemBytes, long workingSetBytes,
        long managedBytes, long committedBytes, long heapLimitBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "API memory anonymous plus shmem recovered below budget: {UnreclaimableBytes} bytes; warning {WarningBytes} bytes")]
    private static partial void LogRecovery(ILogger logger, long unreclaimableBytes, long warningBytes);
}
