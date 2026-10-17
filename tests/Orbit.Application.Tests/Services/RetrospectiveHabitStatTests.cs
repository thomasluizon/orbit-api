using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Services;

public class RetrospectiveHabitStatTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compute_HabitRankingsAndInsightCard_PreserveIdsForDuplicateTitles(bool historical)
    {
        var from = new DateOnly(2026, 3, 2);
        var to = from.AddDays(6);
        var userId = Guid.NewGuid();
        var strong = Habit.Create(new HabitCreateParams(userId, "Read", FrequencyUnit.Day, 1, from)).Value;
        var weak = Habit.Create(new HabitCreateParams(userId, "Read", FrequencyUnit.Day, 1, from)).Value;
        foreach (var habit in new[] { strong, weak })
            typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
                habit, from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        for (var i = 0; i < 7; i++)
            strong.Log(from.AddDays(i), advanceDueDate: false).IsSuccess.Should().BeTrue();
        weak.Log(from, advanceDueDate: false).IsSuccess.Should().BeTrue();

        var metrics = historical
            ? RetrospectiveMetricsCalculator.ComputeHistorical([strong, weak], from, to, 0, 0, TimeZoneInfo.Utc)
            : RetrospectiveMetricsCalculator.Compute([strong, weak], from, to, 0, 0);

        metrics.TopHabits.Select(stat => stat.HabitId).Should().Equal(strong.Id, weak.Id);
        metrics.NeedsAttention.Should().ContainSingle().Which.HabitId.Should().Be(weak.Id);
        var card = PeriodInsightCardBuilder.Build(new RetrospectiveResponse(
            "week", metrics, new RetrospectiveNarrative("Highlights", "Missed", "Trends", "Suggestion"),
            false, from, to));
        card.Should().NotBeNull();
        card!.TopHabits.Select(stat => stat.HabitId).Should().Equal(strong.Id, weak.Id);
        card.NeedsAttention.Should().ContainSingle().Which.HabitId.Should().Be(weak.Id);
    }
}
