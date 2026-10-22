using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public class HabitRankingDefinitionTests
{
    [Theory]
    [InlineData(0, 1, 20, "completion_log_records", true)]
    [InlineData(0, 0, 20, "completion_log_records", false)]
    [InlineData(1, 0, 20, "completion_log_records", false)]
    [InlineData(0, 3661, 20, "completion_log_records", false)]
    [InlineData(0, 1, 0, "completion_log_records", false)]
    [InlineData(0, 1, 101, "completion_log_records", false)]
    [InlineData(0, 1, 20, "distinct_logged_days", false)]
    [InlineData(0, 1, 20, "met_scheduled_dates", false)]
    public void Create_GuardsRangeMetricAndPageBounds(int start, int end, int pageSize, string metric, bool valid)
    {
        var anchor = new DateOnly(2024, 1, 1);
        HabitRankingDefinition.Create(Guid.NewGuid(), anchor.AddDays(start), anchor.AddDays(end),
            "UTC", metric, [], pageSize).IsSuccess.Should().Be(valid);
    }

    [Fact]
    public void Create_RequiresOwnerZoneAndValidTargets_AndCopiesFilters()
    {
        var from = new DateOnly(2024, 1, 1);
        HabitRankingDefinition.Create(Guid.Empty, from, from.AddDays(1), "UTC",
            HabitRankingDefinition.CompletionLogRecords, [], 20).IsFailure.Should().BeTrue();
        HabitRankingDefinition.Create(Guid.NewGuid(), from, from.AddDays(1), "",
            HabitRankingDefinition.CompletionLogRecords, [], 20).IsFailure.Should().BeTrue();
        HabitRankingDefinition.Create(Guid.NewGuid(), from, from.AddDays(1), "UTC",
            HabitRankingDefinition.CompletionLogRecords, [Guid.Empty], 20).IsFailure.Should().BeTrue();
        var id = Guid.NewGuid();
        List<Guid> ids = [id, id];
        var query = HabitRankingDefinition.Create(Guid.NewGuid(), from, from.AddDays(1), "UTC",
            HabitRankingDefinition.CompletionLogRecords, ids, 20).Value;
        ids.Clear();
        query.HabitIds.Should().Equal(id);
    }
}
