using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class BoundedReadStateTests
{
    [Fact]
    public void ReadState_GuardsIdentityUtcAndMonotonicLongestStreak()
    {
        var user = User.Create("State", "read-state@example.test").Value;
        Action invalidExpiry = () => user.SetGoogleTokens("token", "refresh", DateTime.Now);
        invalidExpiry.Should().Throw<ArgumentException>();
        Action invalidAttempt = () => user.MarkCalendarSyncAttempt(DateTime.Now);
        invalidAttempt.Should().Throw<ArgumentException>();
        Action emptyOwner = () => XpDailyTotal.Create(Guid.Empty, new DateOnly(2026, 6, 1), 1, 1);
        emptyOwner.Should().Throw<ArgumentOutOfRangeException>();
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read", FrequencyUnit.Day, 1, new DateOnly(2026, 6, 1))).Value;
        habit.RecordLongestStreak(1000);
        habit.RecordLongestStreak(2);
        habit.LongestRecordedStreak.Should().Be(1000);
        Action negative = () => habit.RecordLongestStreak(-1);
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }
}
