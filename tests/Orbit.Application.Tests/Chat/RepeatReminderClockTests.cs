using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Chat;

public sealed class RepeatReminderClockTests
{
    [Fact]
    public void UnrepresentableNextOccurrence_ReturnsNull()
    {
        var anchorAtUtc = new DateTime(9999, 12, 31, 23, 50, 0, DateTimeKind.Utc);
        ((DateTime?)RepeatReminderClock.Next(anchorAtUtc, new(15), TimeZoneInfo.Utc)).Should().BeNull();
    }

    [Fact]
    public void UnrepresentableActiveWindow_ReturnsNull()
    {
        var instantAtUtc = new DateTime(9999, 12, 31, 20, 0, 0, DateTimeKind.Utc);
        ((DateTime?)RepeatReminderClock.MoveIntoWindow(instantAtUtc, new(15, new(8, 0), new(20, 0)), TimeZoneInfo.Utc))
            .Should().BeNull();
    }

    [Fact]
    public void UnrepresentableWindowUtcOffset_ReturnsNull()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("West", TimeSpan.FromHours(-3), "West", "West");
        var instantAtUtc = new DateTime(9999, 12, 31, 22, 0, 0, DateTimeKind.Utc);
        RepeatReminderClock.MoveIntoWindow(instantAtUtc, new(15, new(23, 0), new(23, 30)), zone)
            .Should().BeNull();
    }

    [Fact]
    public void LatestDue_LastRepresentableCandidateStopsWithoutOverflow()
    {
        var firstAtUtc = new DateTime(9999, 12, 31, 22, 0, 0, DateTimeKind.Utc);
        var nowAtUtc = new DateTime(9999, 12, 31, 23, 50, 0, DateTimeKind.Utc);
        RepeatReminderClock.LatestDue(firstAtUtc, nowAtUtc, new(15, new(22, 0), new(23, 59)), TimeZoneInfo.Utc)
            .Should().Be(new DateTime(9999, 12, 31, 23, 45, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("UTC", 15, 8, 22, 2027, 1)]
    [InlineData("UTC", 420, 22, 6, 2027, 1)]
    [InlineData("UTC", 1380, 0, 23, 2027, 1)]
    [InlineData("America/New_York", 30, 22, 6, 2027, 1)]
    [InlineData("America/New_York", 1380, 0, 23, 2027, 1)]
    [InlineData("America/New_York", 15, 1, 2, 2027, 9)]
    [InlineData("America/New_York", 1380, 0, 23, 2050, 9)]
    [InlineData("America/New_York", 420, 22, 6, 2050, 1)]
    [InlineData("Australia/Lord_Howe", 30, 1, 3, 2027, 8)]
    public void NinetyDayAnchor_MatchesTheStepWalk(string zoneId, int minutes, int startHour, int endHour, int year, int month)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var anchorAtUtc = new DateTime(year, month, 1, 12, 30, 0, DateTimeKind.Utc);
        var options = new RepeatReminderOptions(minutes, new(startHour, 0), new(endHour, 59));
        var firstAtUtc = RepeatReminderClock.Next(anchorAtUtc, options, zone)!.Value;
        var nowAtUtc = anchorAtUtc.AddDays(90);
        var expectedAtUtc = firstAtUtc;
        while (RepeatReminderClock.Next(expectedAtUtc, options, zone) <= nowAtUtc)
            expectedAtUtc = RepeatReminderClock.Next(expectedAtUtc, options, zone)!.Value;

        RepeatReminderClock.LatestDue(firstAtUtc, nowAtUtc, options, zone).Should().Be(expectedAtUtc);
    }

    [Fact]
    public async Task ActiveWindow_CalculationIsBoundedForAnAncientAnchor()
    {
        var firstAtUtc = new DateTime(1, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var nowAtUtc = new DateTime(9990, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var options = new RepeatReminderOptions(15, new(8, 0), new(22, 0));

        var dueAtUtc = await Task.Run(() => RepeatReminderClock.LatestDue(firstAtUtc, nowAtUtc, options, TimeZoneInfo.Utc))
            .WaitAsync(TimeSpan.FromSeconds(2));

        dueAtUtc.Should().Be(nowAtUtc);
    }

    [Fact]
    public void OldAnchor_PreservesTheIntervalPhaseInsideAnActiveWindow()
    {
        var anchorAtUtc = new DateTime(2027, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        var options = new RepeatReminderOptions(1380, new(0, 0), new(23, 59));
        var firstAtUtc = RepeatReminderClock.Next(anchorAtUtc, options, TimeZoneInfo.Utc)!.Value;
        var nowAtUtc = anchorAtUtc.AddDays(8);
        var expectedAtUtc = firstAtUtc;
        while (RepeatReminderClock.Next(expectedAtUtc, options, TimeZoneInfo.Utc) <= nowAtUtc)
            expectedAtUtc = RepeatReminderClock.Next(expectedAtUtc, options, TimeZoneInfo.Utc)!.Value;

        RepeatReminderClock.LatestDue(firstAtUtc, nowAtUtc, options, TimeZoneInfo.Utc)
            .Should().Be(expectedAtUtc);
    }

    [Fact]
    public void AmbiguousActiveWindow_DoesNotMoveNextReminderBeforeTheDose()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var doseAtUtc = new DateTime(2027, 11, 7, 6, 0, 0, DateTimeKind.Utc);
        RepeatReminderClock.Next(doseAtUtc, new RepeatReminderOptions(15, new(1, 30), new(1, 45)), zone)
            .Should().Be(new DateTime(2027, 11, 7, 6, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void SpringForward_MovesAnInvalidWindowStartToTheFirstValidInstant()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var anchorAtUtc = new DateTime(2027, 3, 14, 5, 15, 0, DateTimeKind.Utc);
        RepeatReminderClock.Next(anchorAtUtc, new RepeatReminderOptions(60, new(2, 30), new(4, 0)), zone)
            .Should().Be(new DateTime(2027, 3, 14, 7, 0, 0, DateTimeKind.Utc));
    }
}
