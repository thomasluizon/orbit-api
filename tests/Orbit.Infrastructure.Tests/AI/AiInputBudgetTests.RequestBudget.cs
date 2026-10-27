using FluentAssertions;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiInputBudgetTests
{
    [Theory]
    [InlineData(0, 10, 1, 40_100)]
    [InlineData(30_000, 10, 1, 13_100)]
    [InlineData(30_001, 10, 1, 13_100)]
    [InlineData(30_000, 40, 10, 17_600)]
    [InlineData(30_000, 10, 20, 40_100)]
    [InlineData(50_000, 10, 1, 4_100)]
    [InlineData(-1, 10, 1, 40_100)]
    public async Task RequestBudget_CachedInputUsesConfiguredRateAndKeepsFullReportedUsage(
        int cached, int inputPrice, int cachedPrice, int expected)
    {
        var service = BuildService(new SequenceHandler(ResponsesTestData.Text("Done", 40_000, 100, cached)),
            settings: new AiSettings
            {
                Pricing = new()
                {
                    ["gpt-6-luna"] = new()
                    {
                        InputPerMillionUsd = inputPrice / 100m,
                        CachedInputPerMillionUsd = cachedPrice / 100m
                    }
                }
            });

        var result = await service.SendWithToolsAsync(new AiToolRequest("Hello", "System", []));

        result.IsSuccess.Should().BeTrue();
        result.Value.ReportedTokenCount.Should().Be(40_100);
        result.Value.RequestBudgetTokenCount.Should().Be(expected);
    }

    [Fact]
    public async Task RequestBudget_UnknownPriceCountsEveryToken()
    {
        var service = BuildService(new SequenceHandler(ResponsesTestData.Text("Done", 40_000, 100, 30_000)),
            settings: new AiSettings());

        var result = await service.SendWithToolsAsync(new AiToolRequest("Hello", "System", []));

        result.Value.RequestBudgetTokenCount.Should().Be(40_100);
    }
}
