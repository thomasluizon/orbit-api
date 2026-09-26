using FluentAssertions;
using System.Text.Json;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Services;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Chat;

public class StatusCardBuilderTests
{
    [Fact]
    public void Day_ThreeDueOneDone_HasThirtyThreePercent()
    {
        var today = new DateOnly(2026, 9, 25);
        var habits = Enumerable.Range(0, 3).Select(index =>
            Habit.Create(new HabitCreateParams(Guid.NewGuid(), $"Habit {index}", FrequencyUnit.Day, 1, today)).Value)
            .ToList();
        habits[0].Log(today, advanceDueDate: false);

        var metrics = RetrospectiveMetricsCalculator.Compute(habits, today, today, 2, 4);
        var card = StatusCardBuilder.BuildDay(today, metrics, 0);

        card.Due.Should().Be(3);
        card.Done.Should().Be(1);
        card.CompletionRate.Should().Be(33);
    }

    [Fact]
    public void Day_NothingDue_HasNullRate()
    {
        var today = new DateOnly(2026, 9, 25);
        var metrics = RetrospectiveMetricsCalculator.Compute([], today, today, 0, 0);

        StatusCardBuilder.BuildDay(today, metrics, 0).CompletionRate.Should().BeNull();
    }

    [Fact]
    public void Calendar_TwentyFiveEvents_CapsAtTenAndOmitsGatedSync()
    {
        var events = Enumerable.Range(0, 25)
            .Select(index => new CalendarEventItem(
                index.ToString(), $"Event {index}", null, "2026-09-25", null, null,
                false, null, []))
            .ToList();
        var payload = new CalendarOverviewPayload(events, null, []);

        var card = StatusCardBuilder.BuildCalendar(payload)!;

        card.Events.Should().HaveCount(10);
        card.Sync.Should().BeNull();
        card.Events[0].IsAllDay.Should().BeTrue();
        JsonSerializer.Serialize(card, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().NotContain("\"sync\"");
    }

    [Fact]
    public void Calendar_AllDayWithSyntheticUtcStart_KeepsFloatingDate()
    {
        var events = new List<CalendarEventItem>
        {
            new("event", "Holiday", null, "2026-04-15", null,
                null, false, null, [],
                StartUtc: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc))
        };

        var card = StatusCardBuilder.BuildCalendar(new CalendarOverviewPayload(events, null, []))!;

        card.Events.Should().ContainSingle();
        card.Events[0].Start.Should().Be("2026-04-15");
        card.Events[0].IsAllDay.Should().BeTrue();
    }

    [Fact]
    public void Streak_MissingGatedPayload_OmitsCard()
    {
        StatusCardBuilder.BuildStreak(null).Should().BeNull();
        StatusCardBuilder.BuildStreak(new GamificationOverviewPayload(null, null, null)).Should().BeNull();
    }

    [Fact]
    public void Streak_CopiesRecentFieldsAndCapsAchievementsAtSix()
    {
        var today = new DateOnly(2026, 9, 25);
        var earnedAt = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var achievements = Enumerable.Range(0, 8).Select(index => new AchievementDto(
            $"achievement-{index}", "Name", "Description", "habit", "common", 10,
            "star", true, earnedAt.AddDays(-index))).ToList();
        var profile = new GamificationProfileResponse(
            120, 3, "Level", "level", 100, 200, 80, 8, 8,
            achievements, [], 5, 9, today, true, false,
            new NextRewardCarrot(4, "Next", 80, null));
        var freezeDate = today.AddDays(-2);
        var streak = new StreakInfoResponse(
            5, 9, today, 1, 2, 3, true, [freezeDate],
            0, 3, 0, 2, true, false, null, 0);

        var card = StatusCardBuilder.BuildStreak(new GamificationOverviewPayload(profile,
            new AchievementsResponse(achievements), streak))!;

        card.CurrentStreak.Should().Be(5);
        card.LastActiveDate.Should().Be(today);
        card.IsFrozenToday.Should().BeTrue();
        card.RecentFreezeDates.Should().ContainSingle().Which.Should().Be(freezeDate);
        card.RecentAchievements.Should().HaveCount(6);
    }

    [Fact]
    public void Streak_TwoEarned_FillsSixWithCatalogOrderedUnearned()
    {
        var today = new DateOnly(2026, 9, 25);
        var earnedAt = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var achievements = Enumerable.Range(0, 10).Select(index => new AchievementDto(
            $"achievement-{index}", "Name", "Description", "habit", "common", 10,
            "star", index is 2 or 7, index is 2 or 7 ? earnedAt.AddDays(index) : null)).ToList();
        var profile = new GamificationProfileResponse(
            120, 3, "Level", "level", 100, 200, 80, 8, 8,
            achievements, [], 5, 9, today, true, false,
            new NextRewardCarrot(4, "Next", 80, null));
        var streak = new StreakInfoResponse(
            5, 9, today, 1, 2, 3, true, [],
            0, 3, 0, 2, true, false, null, 0);

        var card = StatusCardBuilder.BuildStreak(new GamificationOverviewPayload(profile,
            new AchievementsResponse(achievements), streak))!;

        card.RecentAchievements.Select(item => item.Id).Should().Equal(
            "achievement-7", "achievement-2", "achievement-0", "achievement-1", "achievement-3", "achievement-4");
        card.RecentAchievements.Count(item => item.EarnedAt.HasValue).Should().Be(2);
    }
}
