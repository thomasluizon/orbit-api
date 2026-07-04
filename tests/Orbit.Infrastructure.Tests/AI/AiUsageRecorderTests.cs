using FluentAssertions;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.AI;

public class AiUsageRecorderTests
{
    private static readonly AiModelPrice SamplePrice = new()
    {
        InputPerMillionUsd = 0.40m,
        CachedInputPerMillionUsd = 0.10m,
        OutputPerMillionUsd = 1.60m
    };

    [Fact]
    public void ComputeCostUsd_UncachedInputPlusOutput_SumsBothLegs()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            SamplePrice, cachedTokens: 0, promptTokens: 1_000_000, completionTokens: 500_000);

        cost.Should().Be(1.20m);
    }

    [Fact]
    public void ComputeCostUsd_SplitsCachedFromUncachedInput()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            SamplePrice, cachedTokens: 200_000, promptTokens: 1_000_000, completionTokens: 0);

        cost.Should().Be(0.34m);
    }

    [Fact]
    public void ComputeCostUsd_AllInputCached_ChargesOnlyCachedRate()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            SamplePrice, cachedTokens: 500_000, promptTokens: 500_000, completionTokens: 0);

        cost.Should().Be(0.05m);
    }

    [Fact]
    public void ComputeCostUsd_OutputOnly_ChargesOutputRate()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            SamplePrice, cachedTokens: 0, promptTokens: 0, completionTokens: 2_000_000);

        cost.Should().Be(3.20m);
    }

    [Fact]
    public void ComputeCostUsd_UnpricedModel_ReturnsZero()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            null, cachedTokens: 1_000, promptTokens: 5_000, completionTokens: 3_000);

        cost.Should().Be(0m);
    }
}
