using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitWeekdayErrorTests
{
    [Theory]
    [InlineData(FrequencyUnit.Week, 1)]
    [InlineData(FrequencyUnit.Day, 2)]
    public void InvalidWeekdaySchedule_ErrorStatesBothRequirements(FrequencyUnit unit, int quantity)
    {
        var result = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            Guid.NewGuid(), "Varrer a garagem", unit, quantity,
            new DateOnly(2026, 8, 6), Days: [DayOfWeek.Thursday]));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.DaysRequireQuantityOne.Code);
        result.Error.Should().Be("Specific weekdays require frequency unit Day and frequency quantity 1.");
    }
}
