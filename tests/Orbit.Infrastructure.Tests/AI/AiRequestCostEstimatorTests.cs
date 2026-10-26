using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.AI;

public sealed class AiRequestCostEstimatorTests(ITestOutputHelper output)
{
    private static readonly AiSettings Settings = new()
    {
        Pricing = new()
        {
            ["gpt-6-luna"] = new() { InputPerMillionUsd = 0.1m, CacheWritePerMillionUsd = 0.125m, OutputPerMillionUsd = 0.5m },
            ["custom-model"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 2m }
        }
    };

    [Fact]
    public async Task RealRequests_ReserveAtLeastProviderInputAndMaximumOutput()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 });
        var estimator = new AiRequestCostEstimator(cache, Options.Create(Settings));
        var fixtures = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AiReservation"), "*.json.gz");
        fixtures.Should().HaveCount(9);
        foreach (var file in fixtures.Order(StringComparer.Ordinal))
        {
            using var stream = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            using var fixture = await JsonDocument.ParseAsync(stream);
            var request = fixture.RootElement.GetProperty("request");
            var providerTokens = fixture.RootElement.GetProperty("completion_usage").GetProperty("input_tokens").GetInt64();
            fixture.RootElement.GetProperty("provider_response").GetProperty("input_tokens").GetInt64()
                .Should().Be(providerTokens);
            var estimatedTokens = estimator.EstimateInputTokens(request);
            estimatedTokens.Should().BeGreaterThanOrEqualTo(providerTokens, Path.GetFileName(file));
            var reserved = await estimator.EstimateMicroDollarsAsync(request.GetRawText(), default);
            reserved.Should().BeGreaterThanOrEqualTo((long)decimal.Ceiling(providerTokens * 0.125m
                + request.GetProperty("max_output_tokens").GetInt32() * 0.5m));
            reserved.Should().BeLessThan(20_000, "a fresh free allowance must still admit each fixture");
            (await estimator.EstimateMicroDollarsAsync(request.GetRawText(), default)).Should().Be(reserved);
            output.WriteLine($"{Path.GetFileName(file)}: provider {providerTokens} input tokens, local bound {estimatedTokens}, reservation {reserved} microdollars");
        }
        cache.Count.Should().BeLessThan(9, "identical prefixes share cached counts");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidOutputLimit_RefusesReservation(int limit)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var estimator = new AiRequestCostEstimator(cache, Options.Create(Settings));
        var action = () => estimator.EstimateMicroDollarsAsync(JsonSerializer.Serialize(new { model = "gpt-6-luna", max_output_tokens = limit }), default);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UnknownPriceAndCancellation_RefuseBeforeEstimating()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var estimator = new AiRequestCostEstimator(cache, Options.Create(Settings));
        var unknown = () => estimator.EstimateMicroDollarsAsync("""{"model":"unpriced","max_output_tokens":1}""", default);
        await unknown.Should().ThrowAsync<InvalidOperationException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () => estimator.EstimateMicroDollarsAsync("{}", cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        cache.Count.Should().Be(0);
    }

    [Fact]
    public void UnrecognizedEncoding_UsesByteBound()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var estimator = new AiRequestCostEstimator(cache, Options.Create(Settings));
        var prefix = new string('a', 4000);
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            model = "custom-model",
            input = new[] { new { role = "system", content = prefix } },
            max_output_tokens = 1
        }));
        estimator.EstimateInputTokens(request.RootElement).Should().BeGreaterThan(Encoding.UTF8.GetByteCount(prefix));
    }
}
