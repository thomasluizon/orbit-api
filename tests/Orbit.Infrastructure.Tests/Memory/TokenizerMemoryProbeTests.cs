using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Memory;

public class TokenizerMemoryProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Estimate_LoadsTheProductionTokenizerAndReportsRetainedMemory()
    {
        Sample("before-tokenizer");
        using var cache = new MemoryCache(new MemoryCacheOptions { TrackStatistics = true });
        var estimator = new AiRequestCostEstimator(cache, Options.Create(new AiSettings
        {
            Pricing = new() { ["gpt-6-luna"] = new() { InputPerMillionUsd = 1, OutputPerMillionUsd = 1 } }
        }));
        for (var round = 0; round < 3; round++)
        {
            for (var index = 0; index < 200; index++)
            {
                var request = JsonSerializer.Serialize(new
                {
                    model = "gpt-6-luna",
                    max_output_tokens = 4096,
                    input = new[]
                    {
                        new { role = "system", content = "Help manage this account's habits." },
                        new { role = "user", content = $"Account {index}, turn {round}: rank my habits. " + new string('x', 10000) }
                    }
                });
                (await estimator.EstimateMicroDollarsAsync(request, default)).Should().BePositive();
            }
            Sample($"loaded-{round}");
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Sample($"retained-{round}");
        }
        output.WriteLine(JsonSerializer.Serialize(cache.GetCurrentStatistics()));
    }

    private void Sample(string phase)
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            phase,
            workingSet = process.WorkingSet64,
            managed = GC.GetTotalMemory(false),
            committed = gc.TotalCommittedBytes,
            heap = gc.HeapSizeBytes,
            limit = gc.TotalAvailableMemoryBytes,
            server = GCSettings.IsServerGC
        }));
    }
}
