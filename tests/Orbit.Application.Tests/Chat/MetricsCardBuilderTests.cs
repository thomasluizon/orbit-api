using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Tests.Chat;

public class MetricsCardBuilderTests
{
    [Fact]
    public void TryExtractDirective_NoDirective_ReturnsFalse()
    {
        var found = MetricsCardBuilder.TryExtractDirective("Your week is ready.", out var stripped);

        found.Should().BeFalse();
        stripped.Should().Be("Your week is ready.");
    }

    [Fact]
    public void TryExtractDirective_TwoTokens_StripsBothAndReturnsOneSignal()
    {
        var found = MetricsCardBuilder.TryExtractDirective(
            "Your week:\n[[orbit:metrics]]\n[[ORBIT:METRICS]]",
            out var stripped);

        found.Should().BeTrue();
        stripped.Should().Be("Your week:");
    }

    [Fact]
    public void TryExtractDirective_MidSentence_RemovesDoubleSpace()
    {
        var found = MetricsCardBuilder.TryExtractDirective(
            "Here is [[orbit:metrics]] your overview.",
            out var stripped);

        found.Should().BeTrue();
        stripped.Should().Be("Here is your overview.");
    }

    [Fact]
    public void Build_MapsOverviewMetricsAndProgressSurface()
    {
        var metrics = Metrics(
            completionRate: 75,
            totalCompletions: 6,
            totalScheduled: 8,
            activeDays: 4,
            currentStreak: 3,
            bestStreak: 9);

        var card = MetricsCardBuilder.Build("week", metrics);

        card.Should().Be(new MetricsCard("week", 75, 6, 8, 4, 3, 9, true, "progress"));
    }

    [Fact]
    public void Build_ZeroMetrics_ReturnsExplicitEmptyCard()
    {
        var card = MetricsCardBuilder.Build("week", Metrics());

        card.HasData.Should().BeFalse();
        card.TotalCompletions.Should().Be(0);
        card.TotalScheduled.Should().Be(0);
    }

    private static RetrospectiveMetrics Metrics(
        int completionRate = 0,
        int totalCompletions = 0,
        int totalScheduled = 0,
        int activeDays = 0,
        int currentStreak = 0,
        int bestStreak = 0) =>
        new(
            completionRate,
            totalCompletions,
            totalScheduled,
            activeDays,
            7,
            currentStreak,
            bestStreak,
            0,
            new int[7],
            [],
            []);
}
