using FluentAssertions;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Commands.Habits;

public class LegacyHabitScheduleTests
{
    [Theory]
    [InlineData(false, false, null, null, HabitSchedule.OneTime)]
    [InlineData(false, false, FrequencyUnit.Day, 1, HabitSchedule.Recurring)]
    [InlineData(false, true, FrequencyUnit.Week, 3, HabitSchedule.Flexible)]
    [InlineData(true, false, null, null, HabitSchedule.General)]
    public void Resolve_PreservesTheFourLegacySchedules(bool general, bool flexible,
        FrequencyUnit? unit, int? quantity, HabitSchedule expected)
    {
        var result = LegacyHabitSchedule.Resolve(general, flexible, unit, quantity);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(true, false, FrequencyUnit.Day, 1, false, false, "GENERAL_HABIT_HAS_FREQUENCY")]
    [InlineData(true, false, null, null, true, false, "GENERAL_HABIT_IS_BAD")]
    [InlineData(false, true, null, null, false, false, "FLEXIBLE_NEEDS_FREQUENCY_UNIT")]
    [InlineData(false, true, FrequencyUnit.Day, 1, false, true, "FLEXIBLE_HAS_DAYS")]
    [InlineData(true, true, null, null, false, false, "FLEXIBLE_NEEDS_FREQUENCY_UNIT")]
    [InlineData(true, true, FrequencyUnit.Day, 1, false, false, "GENERAL_HABIT_HAS_FREQUENCY")]
    public void Resolve_RejectsInvalidFlagsWithExistingCodes(bool general, bool flexible,
        FrequencyUnit? unit, int? quantity, bool bad, bool hasDays, string expected)
    {
        var result = LegacyHabitSchedule.Resolve(general, flexible, unit, quantity, bad,
            hasDays ? [DayOfWeek.Friday] : null);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expected);
    }

    [Fact]
    public void Resolve_DaysStillRequireEveryOneDay()
    {
        LegacyHabitSchedule.Resolve(false, false, FrequencyUnit.Day, 2, days: [DayOfWeek.Friday])
            .ErrorCode.Should().Be(DomainErrors.DaysRequireQuantityOne.Code);
    }
}
