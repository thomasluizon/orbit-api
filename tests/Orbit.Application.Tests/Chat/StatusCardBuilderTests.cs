using FluentAssertions;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, Guid.NewGuid(), $"Habit {index}", FrequencyUnit.Day, 1, today)).Value)
            .ToList();
        habits[0].Log(today, advanceDueDate: false);

        var metrics = RetrospectiveMetricsCalculator.Compute(habits, today, today, 2, 4);
        var card = StatusCardBuilder.BuildDay(today, metrics, 0, []);

        card.Due.Should().Be(3);
        card.Done.Should().Be(1);
        card.CompletionRate.Should().Be(33);
    }

    [Fact]
    public void Day_NothingDue_HasNullRate()
    {
        var today = new DateOnly(2026, 9, 25);
        var metrics = RetrospectiveMetricsCalculator.Compute([], today, today, 0, 0);

        var card = StatusCardBuilder.BuildDay(today, metrics, 0, []);

        card.Rows.Should().BeEmpty();
        card.TotalRows.Should().Be(0);
        card.Due.Should().Be(0);
        card.Done.Should().Be(0);
        card.OverdueCount.Should().Be(0);
        card.CompletionRate.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Day_RowsFollowHabitListOrderAndStatusCapability(bool supportsDoneStatus)
    {
        var today = new DateOnly(2026, 9, 25);
        var userId = Guid.NewGuid();
        var done = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, userId, "Done", null, null, today,
            Position: 0, Emoji: "✅")).Value;
        done.Log(today).IsSuccess.Should().BeTrue();
        var due = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, userId, "Due", null, null, today, Position: 1)).Value;
        var child = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, userId, "Child", null, null, today,
            ParentHabitId: due.Id, Position: 0)).Value;
        var overdue = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, userId, "Overdue", null, null,
            today.AddDays(-1), Position: 2)).Value;
        var bad = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId, "Bad", FrequencyUnit.Day, 1, today,
            IsBadHabit: true, Position: 3)).Value;
        List<Habit> habits = [bad, overdue, child, due, done];
        var rows = HabitListCardBuilder.Build(habits, today, HabitListCardBuilder.ScopeToday,
            supportsDoneStatus).Items;
        var metrics = RetrospectiveMetricsCalculator.Compute(habits, today, today, 0, 0);

        var card = StatusCardBuilder.BuildDay(today, metrics, 1, rows);

        card.Rows.Should().Equal(rows);
        card.Rows!.Select(row => row.Id).Should().Equal(
            done.Id.ToString(), due.Id.ToString(), child.Id.ToString(), overdue.Id.ToString(), bad.Id.ToString());
        card.Rows.Select(row => row.Status).Should().Equal(
            supportsDoneStatus ? "done" : "none", "today", "today", "overdue", "today");
        card.Rows[0].Emoji.Should().Be("✅");
        card.Rows[2].Depth.Should().Be(1);
        card.Rows[4].IsBadHabit.Should().BeTrue();
        card.TotalRows.Should().Be(5);
    }

    [Fact]
    public void Day_TwentyFiveRows_ReturnsFirstTwentyAndFullCount()
    {
        var today = new DateOnly(2026, 9, 25);
        var habits = Enumerable.Range(0, 25).Select(index => Habit.Create(new HabitCreateParams(HabitSchedule.OneTime,
            Guid.NewGuid(), $"Habit {index}", null, null, today, Position: index)).Value).ToList();
        var rows = HabitListCardBuilder.Build(habits, today, HabitListCardBuilder.ScopeToday).Items;
        var metrics = RetrospectiveMetricsCalculator.Compute(habits, today, today, 0, 0);

        var card = StatusCardBuilder.BuildDay(today, metrics, 0, rows);

        card.Rows.Should().Equal(rows.Take(20));
        card.TotalRows.Should().Be(25);
        card.Due.Should().Be(25);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Day_Serialization_PreservesExistingFields(bool includeRows)
    {
        var card = new DaySummaryCard(new DateOnly(2026, 9, 25), 3, 1, 33, 2, 4,
            Rows: includeRows ? [new HabitListCardItem("habit", "Read", null, 0, false, "today")] : null,
            TotalRows: includeRows ? 1 : null);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(card, options)!.AsObject();
        if (!includeRows)
        {
            json.ContainsKey("rows").Should().BeFalse();
            json.ContainsKey("totalRows").Should().BeFalse();
        }
        json.Remove("rows");
        json.Remove("totalRows");
        var legacy = JsonNode.Parse("""{"date":"2026-09-25","due":3,"done":1,"completionRate":33,"overdueCount":2,"currentStreak":4,"surfaceId":"today"}""");

        JsonNode.DeepEquals(json, legacy).Should().BeTrue();
        var deserialized = JsonSerializer.Deserialize<DaySummaryCard>(json.ToJsonString(), options)!;
        deserialized.Rows.Should().BeNull();
        deserialized.TotalRows.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CalendarEvent_Serialization_PreservesExistingFields(bool includeMetadata)
    {
        var card = new CalendarCardEvent("Holiday", "2026-09-25", null, true,
            includeMetadata ? "google-event" : null, includeMetadata ? "Work" : null);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(card, options)!.AsObject();
        if (!includeMetadata)
        {
            json.ContainsKey("id").Should().BeFalse();
            json.ContainsKey("calendarName").Should().BeFalse();
        }
        json.Remove("id");
        json.Remove("calendarName");
        var legacy = JsonNode.Parse("""{"title":"Holiday","start":"2026-09-25","end":null,"isAllDay":true}""");

        JsonNode.DeepEquals(json, legacy).Should().BeTrue();
        var deserialized = JsonSerializer.Deserialize<CalendarCardEvent>(json.ToJsonString(), options)!;
        deserialized.Id.Should().BeNull();
        deserialized.CalendarName.Should().BeNull();
    }

    [Fact]
    public void Calendar_TimedAndAllDayEvents_CopyMetadataAndKeepOrdering()
    {
        var start = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        List<CalendarEventItem> events =
        [
            new("later", "Later", null, "2026-09-25", "14:00", "15:00", false, null, [],
                StartUtc: start.AddHours(2), CalendarName: "Work", EndUtc: start.AddHours(3)),
            new("timed", "Meeting", null, "2026-09-25", "12:00", "13:00", false, null, [],
                StartUtc: start, CalendarName: "Work", EndUtc: start.AddHours(1)),
            new("all-day", "Holiday", null, "2026-09-26", null, null, false, null, [], CalendarName: "Personal"),
            new("earlier-all-day", "Day off", null, "2026-09-24", null, null, false, null, [], CalendarName: "Family")
        ];

        var card = StatusCardBuilder.BuildCalendar(new CalendarOverviewPayload(events, null, []))!;

        card.Events.Select(item => item.Id).Should().Equal("earlier-all-day", "all-day", "timed", "later");
        card.Events.Select(item => item.CalendarName).Should().Equal("Family", "Personal", "Work", "Work");
        card.Events[0].IsAllDay.Should().BeTrue();
        card.Events[0].Start.Should().Be("2026-09-24");
        card.Events[0].End.Should().BeNull();
        card.Events[2].IsAllDay.Should().BeFalse();
        card.Events[2].Start.Should().Be("2026-09-25T12:00:00.0000000Z");
        card.Events[2].End.Should().Be("2026-09-25T13:00:00.0000000Z");
    }

    [Fact]
    public void Calendar_AccountEmailIsNotAnEventRowLabel()
    {
        var item = new CalendarEventItem("meeting", "Team meeting", null, "2026-10-01", null, null,
            false, null, [], CalendarName: "person@example.com");

        var card = StatusCardBuilder.BuildCalendar(new CalendarOverviewPayload([item], null, []))!;

        card.Events.Should().ContainSingle().Which.CalendarName.Should().BeNull();
        card.Events[0].Title.Should().Be(item.Title);
        card.Events[0].Id.Should().Be(item.Id);
        JsonSerializer.Serialize(card).Should().NotContain("person@example.com");
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
        card.Events.Select(item => item.Id).Should().Equal(events.Take(10).Select(item => item.Id));
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

        card.RecentAchievements.Select(item => item.Id).Should().Equal("achievement-7", "achievement-2");
        card.AchievementDiscs!.Select(item => item.Id).Should().Equal(
            "achievement-7", "achievement-2", "achievement-0", "achievement-1", "achievement-3", "achievement-4");
        card.AchievementDiscs!.Count(item => item.EarnedAt.HasValue).Should().Be(2);
    }
}
