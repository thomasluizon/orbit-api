using System.Text.Json;
using FluentAssertions;
using Orbit.Api.Observability;

namespace Orbit.Infrastructure.Tests.Memory;

public class ApiMemoryConfigurationTests
{
    [Fact]
    public void PublishedRuntime_ReservesHalfTheContainerForNativeMemoryAndTransientWork()
    {
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Orbit.Api.runtimeconfig.json")));
        var settings = runtime.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        settings.GetProperty("System.GC.Server").GetBoolean().Should().BeFalse();
        settings.GetProperty("System.GC.Concurrent").GetBoolean().Should().BeTrue();
        settings.GetProperty("System.GC.HeapHardLimit").GetInt64().Should().Be(256L * 1024 * 1024);
        settings.GetProperty("System.GC.ConserveMemory").GetInt32().Should().Be(7);
        settings.GetProperty("System.Runtime.TieredCompilation").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void PublishedRuntime_DoesNotKeepInstrumentedJitCopiesInUnreclaimableMemory()
    {
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Orbit.Api.runtimeconfig.json")));
        var settings = runtime.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        settings.GetProperty("System.Runtime.TieredPGO").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void ContainerGuard_FailsAtTheSameAnonymousPlusShmemLineAsTheProductionMonitor()
    {
        var script = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".github", "scripts", "api-memory-container.sh"));

        script.Should().ContainSingle(line => line.StartsWith("warning_bytes=", StringComparison.Ordinal))
            .Which.Should().Be($"warning_bytes={ApiMemoryBudget.WarningBytes}");
        script.Should().Contain(line => line.Contains("--memory=512m --memory-swap=512m --cpus=0.5", StringComparison.Ordinal));
        ApiMemoryBudget.WarningBytes.Should().BeLessThan(ApiMemoryBudget.ContainerBytes);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Orbit.slnx was not found above the test output.");
    }
}
