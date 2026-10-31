using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class AiUsageDailyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(30)]
    public void Create_PreservesReasoningAsSubsetOfOutput(long reasoning)
    {
        var usage = AiUsageDaily.Create(new DateOnly(2026, 1, 1), "model", "chat",
            new AiUsageTotals(1, 40, 100, 30, 130, 0.1m, reasoning));

        usage.ReasoningTokens.Should().Be(reasoning);
        usage.CompletionTokens.Should().Be(30);
        usage.TotalTokens.Should().Be(130);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void Create_RejectsInvalidReasoningCount(long reasoning)
    {
        var create = () => AiUsageDaily.Create(new DateOnly(2026, 1, 1), "model", "chat",
            new AiUsageTotals(1, 40, 100, 30, 130, 0.1m, reasoning));

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
