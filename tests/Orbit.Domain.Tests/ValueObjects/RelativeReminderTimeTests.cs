using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.ValueObjects;

public class RelativeReminderTimeTests
{
    [Fact]
    public void ScheduledReminderJson_DeserializesAsRelativeClockReminder()
    {
        var scheduled = new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0));
        var storedJson = JsonSerializer.Serialize(scheduled);

        var relative = JsonSerializer.Deserialize<RelativeReminderTime>(storedJson);

        relative.Should().Be(new RelativeReminderTime(
            When: ScheduledReminderWhen.DayBefore,
            Time: new TimeOnly(20, 0)));
    }
}
