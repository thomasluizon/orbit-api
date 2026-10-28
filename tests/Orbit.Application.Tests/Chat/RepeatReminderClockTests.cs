using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Chat;

public sealed class RepeatReminderClockTests
{
    [Fact]
    public void OldAnchor_PreservesTheIntervalPhaseInsideAnActiveWindow()
    {
        var anchorAtUtc = new DateTime(2027, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        var options = new RepeatReminderOptions(1380, new(0, 0), new(23, 59));
        var firstAtUtc = RepeatReminderClock.Next(anchorAtUtc, options, TimeZoneInfo.Utc);
        var nowAtUtc = anchorAtUtc.AddDays(8);
        var expectedAtUtc = firstAtUtc;
        while (RepeatReminderClock.Next(expectedAtUtc, options, TimeZoneInfo.Utc) <= nowAtUtc)
            expectedAtUtc = RepeatReminderClock.Next(expectedAtUtc, options, TimeZoneInfo.Utc);

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
}
