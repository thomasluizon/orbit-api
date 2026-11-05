using System.Text.Json;
using FluentAssertions;

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
}
