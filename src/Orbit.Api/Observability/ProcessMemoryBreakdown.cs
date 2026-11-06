using System.Buffers;
using System.Runtime;
using System.Text;
using System.Text.Json;

namespace Orbit.Api.Observability;

/// <summary>
/// The totals of one kind of mapping in <c>/proc/self/smaps</c>, in kB. For a <c>shm</c> or <c>doublemapper</c>
/// mapping, the resident memory is shmem. The anonymous memory of a file-backed mapping holds its copied-on-write pages.
/// </summary>
internal sealed record MemoryMappingTotal(string Kind, int Mappings, long SizeKb, long RssKb, long AnonymousKb);

/// <summary>One mapping of <c>/proc/self/smaps</c> without its path, in kB.</summary>
internal sealed record MemoryMapping(string Kind, string Permissions, long SizeKb, long RssKb, long AnonymousKb);

/// <summary>The kinds and the largest resident mappings of <c>/proc/self/smaps</c>.</summary>
internal sealed record MemoryMapSummary(IReadOnlyList<MemoryMappingTotal> Kinds, IReadOnlyList<MemoryMapping> Largest,
    int UnparsedLines);

/// <summary>
/// Counters of the runtime that own memory outside the GC heap, or that tell where the GC heap is.
/// <see cref="Generations"/> holds generation 0, 1, 2, the large object heap and the pinned object heap after the last GC.
/// </summary>
internal sealed record RuntimeMemory(long HeapSizeBytes, long FragmentedBytes, long CommittedBytes, long PromotedBytes,
    long PinnedObjectsCount, long FinalizationPendingCount, IReadOnlyList<(long SizeBytes, long FragmentationBytes)> Generations,
    IReadOnlyList<int> Collections, long AllocatedBytes, int ThreadPoolThreads, long ThreadPoolPendingItems,
    long ActiveTimers, int Assemblies, int DynamicAssemblies, long JitMethods, long JitILBytes, bool ServerGC)
{
    internal static RuntimeMemory Read()
    {
        var gc = GC.GetGCMemoryInfo();
        var generations = new List<(long, long)>(gc.GenerationInfo.Length);
        foreach (var generation in gc.GenerationInfo)
            generations.Add((generation.SizeAfterBytes, generation.FragmentationAfterBytes));
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        return new(gc.HeapSizeBytes, gc.FragmentedBytes, gc.TotalCommittedBytes, gc.PromotedBytes,
            gc.PinnedObjectsCount, gc.FinalizationPendingCount, generations,
            [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)], GC.GetTotalAllocatedBytes(),
            ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, Timer.ActiveCount, assemblies.Length,
            assemblies.Count(assembly => assembly.IsDynamic), System.Runtime.JitInfo.GetCompiledMethodCount(),
            System.Runtime.JitInfo.GetCompiledILBytes(), GCSettings.IsServerGC);
    }
}

/// <summary>
/// A bounded description of where the process memory is: the kB fields of <c>/proc/self/smaps_rollup</c>, the mappings
/// of <c>/proc/self/smaps</c> grouped by kind, the largest resident mappings, the thread count and the GC state.
/// The text holds kind names and numbers only. It never holds a mapping path, so it cannot show a file name or a secret.
/// The kernel fields are described in https://docs.kernel.org/filesystems/proc.html
/// </summary>
internal static class ProcessMemoryBreakdown
{
    internal const string SmapsRollupPath = "/proc/self/smaps_rollup";
    internal const string SmapsPath = "/proc/self/smaps";
    internal const int MaxLength = 4000;
    internal const int LargestMappingCount = 12;

    private static readonly string[] RollupFields =
    [
        "Rss", "Pss_Anon", "Pss_File", "Pss_Shmem", "Shared_Clean", "Shared_Dirty", "Private_Clean", "Private_Dirty",
        "Anonymous", "AnonHugePages", "Swap",
    ];

    internal static string Read(TimeSpan uptime) =>
        Format(uptime,
            ReadSource(SmapsRollupPath, ParseRollup),
            ReadSource(SmapsPath, lines => ParseSmaps(lines, LargestMappingCount)),
            ReadSource(ProcessMemorySnapshot.StatusPath, ParseThreads),
            RuntimeMemory.Read(), MaxLength);

    /// <summary>
    /// The value of the source, or null without <c>/proc</c> (local development off Linux). A refused read gives
    /// <see cref="SourceRead{T}.Unavailable"/>, which the text reports by the <c>/proc/self</c> path.
    /// </summary>
    internal static SourceRead<T> ReadSource<T>(string path, Func<IEnumerable<string>, T> parse) where T : class =>
        ReadSource(() => File.Exists(path) ? File.ReadLines(path) : null, path, parse);

    internal static SourceRead<T> ReadSource<T>(Func<IEnumerable<string>?> readLines, string path,
        Func<IEnumerable<string>, T> parse) where T : class
    {
        try
        {
            return readLines() is { } lines ? new(parse(lines), null) : new(null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(null, path);
        }
    }

    internal static IReadOnlyDictionary<string, long> ParseRollup(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = line[..colon];
            if (Array.IndexOf(RollupFields, name) >= 0 && ProcessMemorySnapshot.TryParseKilobytes(line, out var kilobytes))
                values[name] = kilobytes;
        }
        return values;
    }

    internal static StatusThreads ParseThreads(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith("Threads:", StringComparison.Ordinal)) continue;
            return int.TryParse(line.AsSpan("Threads:".Length).Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var threads)
                ? new(threads)
                : new(null);
        }
        return new(null);
    }

    internal static MemoryMapSummary ParseSmaps(IEnumerable<string> lines, int largestCount)
    {
        var totals = new Dictionary<string, MemoryMappingTotal>(StringComparer.Ordinal);
        var largest = new List<MemoryMapping>(largestCount + 1);
        MemoryMapping? current = null;
        var unparsed = 0;

        void Flush()
        {
            if (current is null) return;
            totals[current.Kind] = totals.TryGetValue(current.Kind, out var total)
                ? total with
                {
                    Mappings = total.Mappings + 1,
                    SizeKb = total.SizeKb + current.SizeKb,
                    RssKb = total.RssKb + current.RssKb,
                    AnonymousKb = total.AnonymousKb + current.AnonymousKb,
                }
                : new(current.Kind, 1, current.SizeKb, current.RssKb, current.AnonymousKb);
            var index = largest.FindIndex(mapping => mapping.RssKb < current.RssKb);
            largest.Insert(index < 0 ? largest.Count : index, current);
            if (largest.Count > largestCount) largest.RemoveAt(largest.Count - 1);
        }

        foreach (var line in lines)
        {
            if (IsMappingHeader(line))
            {
                Flush();
                var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                current = new(Classify(fields.Length > 5 ? fields[5] : null), fields[1], 0, 0, 0);
                continue;
            }
            if (current is null) continue;
            if (line.StartsWith("Size:", StringComparison.Ordinal))
                current = ProcessMemorySnapshot.TryParseKilobytes(line, out var size) ? current with { SizeKb = size } : Count(current, ref unparsed);
            else if (line.StartsWith("Rss:", StringComparison.Ordinal))
                current = ProcessMemorySnapshot.TryParseKilobytes(line, out var rss) ? current with { RssKb = rss } : Count(current, ref unparsed);
            else if (line.StartsWith("Anonymous:", StringComparison.Ordinal))
                current = ProcessMemorySnapshot.TryParseKilobytes(line, out var anonymous) ? current with { AnonymousKb = anonymous } : Count(current, ref unparsed);
        }
        Flush();

        return new([.. totals.Values.OrderByDescending(total => total.RssKb).ThenBy(total => total.Kind, StringComparer.Ordinal)],
            largest, unparsed);
    }

    private static MemoryMapping Count(MemoryMapping mapping, ref int unparsed)
    {
        unparsed++;
        return mapping;
    }

    /// <summary>
    /// A mapping header is <c>start-end perms offset dev inode [path]</c>; every other line of a mapping starts with a
    /// field name and a colon.
    /// </summary>
    private static bool IsMappingHeader(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0 || line[space - 1] == ':') return false;
        var dash = line.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && dash < space && Uri.IsHexDigit(line[0]);
    }

    /// <summary>
    /// The kind of a mapping path. <c>/memfd:doublemapper</c> is the runtime's write xor execute mapping of executable
    /// memory and loader data.
    /// </summary>
    internal static string Classify(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith("[anon:", StringComparison.Ordinal)) return "anonymous";
        if (path == "[heap]") return "heap";
        if (path == "[stack]") return "stack";
        if (path.StartsWith('[')) return "kernel";
        if (path.StartsWith("/memfd:doublemapper", StringComparison.Ordinal)) return "doublemapper";
        if (path.StartsWith("/memfd:", StringComparison.Ordinal) || path.StartsWith("/dev/shm/", StringComparison.Ordinal)
            || path.StartsWith("/SYSV", StringComparison.Ordinal))
            return "shm";
        var name = Path.GetFileName(path.EndsWith(" (deleted)", StringComparison.Ordinal) ? path[..^" (deleted)".Length] : path);
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return "dll";
        if (name.EndsWith(".so", StringComparison.Ordinal) || name.Contains(".so.", StringComparison.Ordinal)) return "so";
        return "file";
    }

    /// <summary>
    /// Compact JSON of at most <paramref name="maxLength"/> characters. When the text is too long, the list of the
    /// largest mappings becomes shorter; the totals by kind stay.
    /// </summary>
    internal static string Format(TimeSpan uptime, SourceRead<IReadOnlyDictionary<string, long>> rollup,
        SourceRead<MemoryMapSummary> smaps, SourceRead<StatusThreads> status, RuntimeMemory runtime, int maxLength)
    {
        var largestCount = smaps.Value?.Largest.Count ?? 0;
        while (true)
        {
            var text = Write(uptime, rollup, smaps, status, runtime, largestCount);
            if (text.Length <= maxLength) return text;
            if (largestCount == 0) return text[..maxLength];
            largestCount--;
        }
    }

    private static string Write(TimeSpan uptime, SourceRead<IReadOnlyDictionary<string, long>> rollup,
        SourceRead<MemoryMapSummary> smaps, SourceRead<StatusThreads> status, RuntimeMemory runtime, int largestCount)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("uptimeSeconds", (long)uptime.TotalSeconds);
            string?[] unavailable = [rollup.UnavailablePath, smaps.UnavailablePath, status.UnavailablePath];
            if (unavailable.Any(path => path is not null))
            {
                json.WriteStartArray("unavailable");
                foreach (var path in unavailable)
                    if (path is not null) json.WriteStringValue(path);
                json.WriteEndArray();
            }
            if (rollup.Value is { } rollupValues)
            {
                json.WriteStartObject("rollupKb");
                foreach (var field in RollupFields)
                    if (rollupValues.TryGetValue(field, out var kilobytes)) json.WriteNumber(field, kilobytes);
                json.WriteEndObject();
            }
            if (smaps.Value is { } map)
            {
                json.WriteStartArray("kindsKb");
                foreach (var total in map.Kinds)
                {
                    json.WriteStartObject();
                    json.WriteString("kind", total.Kind);
                    json.WriteNumber("count", total.Mappings);
                    json.WriteNumber("size", total.SizeKb);
                    json.WriteNumber("rss", total.RssKb);
                    json.WriteNumber("anon", total.AnonymousKb);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("largestKb");
                foreach (var mapping in map.Largest.Take(largestCount))
                {
                    json.WriteStartObject();
                    json.WriteString("kind", mapping.Kind);
                    json.WriteString("perms", mapping.Permissions);
                    json.WriteNumber("size", mapping.SizeKb);
                    json.WriteNumber("rss", mapping.RssKb);
                    json.WriteNumber("anon", mapping.AnonymousKb);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                if (map.UnparsedLines > 0) json.WriteNumber("unparsedLines", map.UnparsedLines);
            }
            if (status.Value?.Threads is { } threads) json.WriteNumber("threads", threads);
            json.WriteStartObject("runtime");
            json.WriteNumber("threadPoolThreads", runtime.ThreadPoolThreads);
            json.WriteNumber("threadPoolPending", runtime.ThreadPoolPendingItems);
            json.WriteNumber("timers", runtime.ActiveTimers);
            json.WriteNumber("assemblies", runtime.Assemblies);
            json.WriteNumber("dynamicAssemblies", runtime.DynamicAssemblies);
            json.WriteNumber("jitMethods", runtime.JitMethods);
            json.WriteNumber("jitILBytes", runtime.JitILBytes);
            json.WriteEndObject();
            json.WriteStartObject("gc");
            json.WriteBoolean("server", runtime.ServerGC);
            json.WriteNumber("heapSize", runtime.HeapSizeBytes);
            json.WriteNumber("fragmented", runtime.FragmentedBytes);
            json.WriteNumber("committed", runtime.CommittedBytes);
            json.WriteNumber("promoted", runtime.PromotedBytes);
            json.WriteNumber("pinnedObjects", runtime.PinnedObjectsCount);
            json.WriteNumber("finalizationPending", runtime.FinalizationPendingCount);
            json.WriteNumber("allocated", runtime.AllocatedBytes);
            json.WriteStartArray("collections");
            foreach (var count in runtime.Collections) json.WriteNumberValue(count);
            json.WriteEndArray();
            json.WriteStartArray("generations");
            foreach (var (size, fragmentation) in runtime.Generations)
            {
                json.WriteStartArray();
                json.WriteNumberValue(size);
                json.WriteNumberValue(fragmentation);
                json.WriteEndArray();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>A parsed <c>/proc/self</c> file. <see cref="UnavailablePath"/> names a file whose read the kernel refused.</summary>
internal readonly record struct SourceRead<T>(T? Value, string? UnavailablePath) where T : class;

/// <summary>The <c>Threads</c> field of <c>/proc/self/status</c>, or null when the field is missing or malformed.</summary>
internal sealed record StatusThreads(int? Threads);
