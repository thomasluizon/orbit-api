using System.Diagnostics;
using System.Runtime;
using System.Text.Json;

namespace Orbit.Api.Observability;

/// <summary>
/// The <c>RssAnon</c> and <c>RssShmem</c> fields of <c>/proc/self/status</c>. <see cref="UnavailableField"/> names
/// the first field that is missing or malformed; the byte values are then zero.
/// </summary>
internal readonly record struct ProcessStatusMemory(long AnonymousBytes, long ShmemBytes, string? UnavailableField)
{
    internal static ProcessStatusMemory Unavailable(string field) => new(0, 0, field);
}

/// <summary>
/// Process memory at one sample. <see cref="UnreclaimableBytes"/> is anonymous plus shmem memory: the kernel
/// cannot reclaim it without swap, so it decides an out-of-memory kill. File pages in the working set are reclaimable.
/// Without <c>/proc</c> (local development off Linux), or when <see cref="UnavailableStatusField"/> names a status
/// field that the kernel did not report, the whole working set counts as anonymous memory.
/// </summary>
internal sealed record ProcessMemorySnapshot(long WorkingSetBytes, long AnonymousBytes, long ShmemBytes,
    long ManagedBytes, long CommittedBytes, long HeapLimitBytes, string? UnavailableStatusField = null)
{
    internal const string StatusPath = "/proc/self/status";

    internal long UnreclaimableBytes => AnonymousBytes + ShmemBytes;

    internal static ProcessMemorySnapshot Read() =>
        Read(() => File.Exists(StatusPath) ? File.ReadLines(StatusPath) : null);

    internal static ProcessMemorySnapshot Read(Func<IEnumerable<string>?> readStatusLines)
    {
        using var process = Process.GetCurrentProcess();
        var workingSetBytes = process.WorkingSet64;
        var gc = GC.GetGCMemoryInfo();
        var status = ReadStatus(readStatusLines);
        var (anonymousBytes, shmemBytes) = status is { UnavailableField: null } available
            ? (available.AnonymousBytes, available.ShmemBytes)
            : (workingSetBytes, 0L);
        return new(workingSetBytes, anonymousBytes, shmemBytes, GC.GetTotalMemory(false),
            gc.TotalCommittedBytes, gc.TotalAvailableMemoryBytes, status?.UnavailableField);
    }

    private static ProcessStatusMemory? ReadStatus(Func<IEnumerable<string>?> readStatusLines)
    {
        try
        {
            return readStatusLines() is { } lines ? ParseStatus(lines) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ProcessStatusMemory.Unavailable(StatusPath);
        }
    }

    /// <summary>
    /// Reads <c>RssAnon</c> and <c>RssShmem</c>, which the kernel reports in kB since Linux 4.5:
    /// https://docs.kernel.org/filesystems/proc.html
    /// </summary>
    internal static ProcessStatusMemory ParseStatus(IEnumerable<string> lines)
    {
        long? anonymousBytes = null, shmemBytes = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("RssAnon:", StringComparison.Ordinal))
            {
                if (!TryParseKilobytes(line, out var bytes)) return ProcessStatusMemory.Unavailable("RssAnon");
                anonymousBytes = bytes;
            }
            else if (line.StartsWith("RssShmem:", StringComparison.Ordinal))
            {
                if (!TryParseKilobytes(line, out var bytes)) return ProcessStatusMemory.Unavailable("RssShmem");
                shmemBytes = bytes;
            }
        }
        if (anonymousBytes is not { } anonymous) return ProcessStatusMemory.Unavailable("RssAnon");
        if (shmemBytes is not { } shmem) return ProcessStatusMemory.Unavailable("RssShmem");
        return new(anonymous, shmem, null);
    }

    private static bool TryParseKilobytes(string line, out long bytes)
    {
        bytes = 0;
        var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[2] != "kB"
            || !long.TryParse(fields[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var kilobytes)
            || kilobytes > long.MaxValue / 1024)
            return false;
        bytes = kilobytes * 1024;
        return true;
    }
}

internal sealed partial class ProcessMemoryMonitor(ILogger<ProcessMemoryMonitor> logger, TimeProvider time) : BackgroundService
{
    private readonly Func<ProcessMemorySnapshot> _readSnapshot = ProcessMemorySnapshot.Read;
    private bool _aboveBudget;
    private bool _statusUnavailableLogged;
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
        if (snapshot.UnavailableStatusField is { } field && !_statusUnavailableLogged)
        {
            LogStatusUnavailable(logger, field, ProcessMemorySnapshot.StatusPath);
            _statusUnavailableLogged = true;
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "API memory status field {StatusField} is unavailable in {StatusPath}; the monitor decides on the working set")]
    private static partial void LogStatusUnavailable(ILogger logger, string statusField, string statusPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "API memory budget exceeded: anonymous plus shmem {UnreclaimableBytes} bytes ({UnreclaimablePercent:F1}% of {ContainerLimitBytes}); anonymous {AnonymousBytes} bytes; shmem {ShmemBytes} bytes; working set {WorkingSetBytes} bytes; managed {ManagedBytes} bytes; committed heap {CommittedBytes} bytes; heap limit {HeapLimitBytes} bytes")]
    private static partial void LogBudgetExceeded(ILogger logger, long unreclaimableBytes, double unreclaimablePercent,
        long containerLimitBytes, long anonymousBytes, long shmemBytes, long workingSetBytes,
        long managedBytes, long committedBytes, long heapLimitBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "API memory anonymous plus shmem recovered below budget: {UnreclaimableBytes} bytes; warning {WarningBytes} bytes")]
    private static partial void LogRecovery(ILogger logger, long unreclaimableBytes, long warningBytes);
}
