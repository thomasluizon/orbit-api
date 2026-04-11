using FluentAssertions;
using Google.Apis.Calendar.v3.Data;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Calendar;

public class CalendarEventFetcherBuildRemindersTests
{
    [Fact]
    public void BuildReminders_NullStartTime_ReturnsOverrideRemindersOnly()
    {
        var ev = new Event
        {
            Reminders = new Event.RemindersData
            {
                Overrides = new List<EventReminder>
                {
                    new() { Minutes = 30 },
                    new() { Minutes = 60 }
                }
            }
        };

        var result = CalendarEventFetcher.BuildReminders(ev, null);

        result.Should().BeEquivalentTo(new List<int> { 30, 60 });
    }

    [Fact]
    public void BuildReminders_NullStartTime_NoOverrides_ReturnsEmptyList()
    {
        var ev = new Event();
        var result = CalendarEventFetcher.BuildReminders(ev, null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildReminders_WithStartTime_NoOverrides_AddsDefaultAndZero()
    {
        var ev = new Event();
        var result = CalendarEventFetcher.BuildReminders(ev, "09:00");

        result.Should().Contain(AppConstants.DefaultReminderMinutes);
        result.Should().Contain(0);
    }

    [Fact]
    public void BuildReminders_WithStartTime_WithOverrides_AddsZeroIfMissing()
    {
        var ev = new Event
        {
            Reminders = new Event.RemindersData
            {
                Overrides = new List<EventReminder> { new() { Minutes = 30 } }
            }
        };

        var result = CalendarEventFetcher.BuildReminders(ev, "14:00");

        result.Should().Contain(30);
        result.Should().Contain(0);
        result.Should().NotContain(AppConstants.DefaultReminderMinutes);
    }

    [Fact]
    public void BuildReminders_WithStartTime_OverridesIncludeZero_DoesNotDuplicate()
    {
        var ev = new Event
        {
            Reminders = new Event.RemindersData
            {
                Overrides = new List<EventReminder>
                {
                    new() { Minutes = 15 },
                    new() { Minutes = 0 }
                }
            }
        };

        var result = CalendarEventFetcher.BuildReminders(ev, "10:00");
        result.Count(r => r == 0).Should().Be(1);
    }

    [Fact]
    public void BuildReminders_DuplicateOverrideMinutes_DeduplicatesCorrectly()
    {
        var ev = new Event
        {
            Reminders = new Event.RemindersData
            {
                Overrides = new List<EventReminder>
                {
                    new() { Minutes = 30 },
                    new() { Minutes = 30 },
                    new() { Minutes = 60 }
                }
            }
        };

        var result = CalendarEventFetcher.BuildReminders(ev, null);
        result.Count(r => r == 30).Should().Be(1);
        result.Should().Contain(60);
    }

    [Fact]
    public void BuildReminders_NullMinutesInOverrides_SkipsThem()
    {
        var ev = new Event
        {
            Reminders = new Event.RemindersData
            {
                Overrides = new List<EventReminder>
                {
                    new() { Minutes = 15 },
                    new() { Minutes = null }
                }
            }
        };

        var result = CalendarEventFetcher.BuildReminders(ev, null);
        result.Should().ContainSingle().Which.Should().Be(15);
    }
}
