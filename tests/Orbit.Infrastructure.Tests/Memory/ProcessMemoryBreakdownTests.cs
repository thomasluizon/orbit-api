using System.Text.Json;
using FluentAssertions;
using Orbit.Api.Observability;

namespace Orbit.Infrastructure.Tests.Memory;

/// <summary>
/// The fixtures come from a .NET 10 ASP.NET Core process in a Linux arm64 container with a 512 MiB limit.
/// <c>smaps.txt</c> keeps the six largest mappings of each path kind out of 1,392; <c>smaps_rollup.txt</c> is whole.
/// The expected totals were added up from the fixture by a separate awk script, not by the parser under test.
/// </summary>
public class ProcessMemoryBreakdownTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Memory");

    private static IEnumerable<string> Fixture(string name) => File.ReadLines(Path.Combine(FixtureDirectory, name));

    [Fact]
    public void ParseSmaps_GroupsTheKernelMappingsByKindAndKeepsTheLargestResidentMappings()
    {
        var summary = ProcessMemoryBreakdown.ParseSmaps(Fixture("smaps.txt"), 3);

        summary.Kinds.Should().Equal(
            new MemoryMappingTotal("dll", 6, 41592, 16168, 236),
            new MemoryMappingTotal("so", 6, 17184, 13164, 0),
            new MemoryMappingTotal("anonymous", 6, 4440, 4308, 4308),
            new MemoryMappingTotal("heap", 1, 2932, 2656, 2656),
            new MemoryMappingTotal("doublemapper", 6, 2368, 2296, 0),
            new MemoryMappingTotal("file", 4, 80, 80, 8),
            new MemoryMappingTotal("stack", 1, 132, 60, 60),
            new MemoryMappingTotal("kernel", 2, 24, 4, 0),
            new MemoryMappingTotal("shm", 1, 4, 0, 0));
        summary.Largest.Select(mapping => (mapping.Kind, mapping.Permissions, mapping.RssKb)).Should().Equal(
            ("dll", "r-xp", 8000L), ("so", "r-xp", 4292L), ("so", "r-xp", 3112L));
        summary.UnparsedLines.Should().Be(0);
    }

    [Fact]
    public void ParseSmaps_CountsAMalformedSizeLineAndKeepsTheOtherFields()
    {
        string[] smaps =
        [
            "7f0000000000-7f0000100000 rw-p 00000000 00:00 0 ",
            "Size:               one kB",
            "Rss:                 512 kB",
            "Anonymous:           512 kB",
            "VmFlags: rd wr mr mw me ac ",
        ];

        var summary = ProcessMemoryBreakdown.ParseSmaps(smaps, 12);

        summary.UnparsedLines.Should().Be(1);
        summary.Kinds.Should().Equal(new MemoryMappingTotal("anonymous", 1, 0, 512, 512));
    }

    [Theory]
    [InlineData(null, "anonymous")]
    [InlineData("", "anonymous")]
    [InlineData("[anon:dotnet-gc]", "anonymous")]
    [InlineData("[heap]", "heap")]
    [InlineData("[stack]", "stack")]
    [InlineData("[vdso]", "kernel")]
    [InlineData("/memfd:doublemapper (deleted)", "doublemapper")]
    [InlineData("/memfd:dotnet_ipc_created (deleted)", "shm")]
    [InlineData("/dev/shm/orbit", "shm")]
    [InlineData("/SYSV00000000 (deleted)", "shm")]
    [InlineData("/app/Orbit.Api.dll", "dll")]
    [InlineData("/app/Orbit.Api.dll (deleted)", "dll")]
    [InlineData("/usr/lib/aarch64-linux-gnu/libc.so.6", "so")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so", "so")]
    [InlineData("/usr/share/dotnet/dotnet", "file")]
    [InlineData("/app/appsettings.json", "file")]
    public void Classify_NamesTheKindOfAMappingPath(string? path, string kind)
    {
        ProcessMemoryBreakdown.Classify(path).Should().Be(kind);
    }

    [Fact]
    public void ParseRollup_ReadsTheKilobyteFieldsAfterTheRollupHeader()
    {
        var rollup = ProcessMemoryBreakdown.ParseRollup(Fixture("smaps_rollup.txt"));

        rollup.Should().Contain("Rss", 72120).And.Contain("Pss_Anon", 10904).And.Contain("Pss_Shmem", 7891)
            .And.Contain("Anonymous", 10904).And.Contain("Private_Dirty", 18732).And.Contain("Swap", 0)
            .And.NotContainKey("Pss").And.NotContainKey("KSM");
    }

    [Theory]
    [InlineData(37, "Name:\tdotnet", "Threads:\t37", "VmRSS:\t1 kB")]
    [InlineData(null, "Name:\tdotnet", "Threads:\tmany")]
    [InlineData(null, "Name:\tdotnet")]
    public void ParseThreads_ReadsTheThreadCountOfTheStatus(int? threads, params string[] status)
    {
        ProcessMemoryBreakdown.ParseThreads(status).Threads.Should().Be(threads);
    }

    [Fact]
    public void ReadSource_NamesTheProcFileWhenTheKernelRefusesTheRead()
    {
        var read = ProcessMemoryBreakdown.ReadSource<StatusThreads>(() => throw new UnauthorizedAccessException("denied"),
            ProcessMemoryBreakdown.SmapsPath, ProcessMemoryBreakdown.ParseThreads);

        read.Should().Be(new SourceRead<StatusThreads>(null, ProcessMemoryBreakdown.SmapsPath));
    }

    [Fact]
    public void Format_WritesNumbersAndKindsButNoMappingPath()
    {
        var text = ProcessMemoryBreakdown.Format(TimeSpan.FromHours(9.5), FixtureRollup(),
            new(ProcessMemoryBreakdown.ParseSmaps(Fixture("smaps.txt"), ProcessMemoryBreakdown.LargestMappingCount), null),
            new(new StatusThreads(37), null), Runtime(), ProcessMemoryBreakdown.MaxLength);

        text.Should().NotContain("/").And.NotContain("dotnet_ipc").And.NotContain("CoreLib");
        text.Length.Should().BeLessThanOrEqualTo(ProcessMemoryBreakdown.MaxLength);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        root.GetProperty("uptimeSeconds").GetInt64().Should().Be(34200);
        root.GetProperty("rollupKb").GetProperty("Pss_Shmem").GetInt64().Should().Be(7891);
        root.GetProperty("kindsKb")[0].GetProperty("kind").GetString().Should().Be("dll");
        root.GetProperty("kindsKb")[0].GetProperty("rss").GetInt64().Should().Be(16168);
        root.GetProperty("largestKb").GetArrayLength().Should().Be(ProcessMemoryBreakdown.LargestMappingCount);
        root.GetProperty("threads").GetInt32().Should().Be(37);
        root.GetProperty("runtime").GetProperty("dynamicAssemblies").GetInt32().Should().Be(2);
        root.GetProperty("gc").GetProperty("fragmented").GetInt64().Should().Be(7);
        root.GetProperty("gc").GetProperty("generations").GetArrayLength().Should().Be(5);
        root.TryGetProperty("unavailable", out _).Should().BeFalse();
    }

    [Fact]
    public void Format_ShortensTheLargestMappingsFirstAndNeverExceedsTheCap()
    {
        var smaps = new SourceRead<MemoryMapSummary>(
            ProcessMemoryBreakdown.ParseSmaps(Fixture("smaps.txt"), ProcessMemoryBreakdown.LargestMappingCount), null);
        var whole = ProcessMemoryBreakdown.Format(TimeSpan.Zero, FixtureRollup(), smaps, new(null, null), Runtime(), int.MaxValue);
        var cap = whole.Length - 1;

        var shortened = ProcessMemoryBreakdown.Format(TimeSpan.Zero, FixtureRollup(), smaps, new(null, null), Runtime(), cap);

        shortened.Length.Should().BeLessThanOrEqualTo(cap);
        using var json = JsonDocument.Parse(shortened);
        json.RootElement.GetProperty("largestKb").GetArrayLength().Should().Be(ProcessMemoryBreakdown.LargestMappingCount - 1);
        json.RootElement.GetProperty("kindsKb").GetArrayLength().Should().Be(9);

        ProcessMemoryBreakdown.Format(TimeSpan.Zero, FixtureRollup(), smaps, new(null, null), Runtime(), 40)
            .Should().HaveLength(40);
    }

    [Fact]
    public void Format_NamesTheRefusedProcFilesAndOmitsTheirValues()
    {
        var text = ProcessMemoryBreakdown.Format(TimeSpan.Zero, new(null, ProcessMemoryBreakdown.SmapsRollupPath),
            new(null, ProcessMemoryBreakdown.SmapsPath), new(null, null), Runtime(), ProcessMemoryBreakdown.MaxLength);

        using var json = JsonDocument.Parse(text);
        json.RootElement.GetProperty("unavailable").EnumerateArray().Select(path => path.GetString())
            .Should().Equal("/proc/self/smaps_rollup", "/proc/self/smaps");
        json.RootElement.TryGetProperty("rollupKb", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("kindsKb", out _).Should().BeFalse();
    }

    [Fact]
    public void Read_DescribesTheCurrentProcessWithinTheCap()
    {
        var text = ProcessMemoryBreakdown.Read(TimeSpan.FromSeconds(90));

        text.Length.Should().BeLessThanOrEqualTo(ProcessMemoryBreakdown.MaxLength);
        using var json = JsonDocument.Parse(text);
        json.RootElement.GetProperty("uptimeSeconds").GetInt64().Should().Be(90);
        json.RootElement.GetProperty("runtime").GetProperty("assemblies").GetInt32().Should().BePositive();
        json.RootElement.GetProperty("gc").GetProperty("committed").GetInt64().Should().BePositive();
        json.RootElement.TryGetProperty("unavailable", out _).Should().BeFalse();
        if (OperatingSystem.IsLinux())
        {
            json.RootElement.GetProperty("kindsKb").GetArrayLength().Should().BePositive();
            json.RootElement.GetProperty("rollupKb").GetProperty("Rss").GetInt64().Should().BePositive();
            json.RootElement.GetProperty("threads").GetInt32().Should().BePositive();
        }
    }

    private static SourceRead<IReadOnlyDictionary<string, long>> FixtureRollup() =>
        new(ProcessMemoryBreakdown.ParseRollup(Fixture("smaps_rollup.txt")), null);

    private static RuntimeMemory Runtime() =>
        new(1, 7, 3, 4, 5, 6, [(10, 1), (20, 2), (30, 3), (40, 4), (50, 5)], [9, 8, 7], 11, 12, 13, 14, 200, 2, 15, 16, false);
}
