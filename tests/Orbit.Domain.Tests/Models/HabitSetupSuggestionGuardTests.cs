using FluentAssertions;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public class HabitSetupSuggestionGuardTests
{
    private static HabitSetupSuggestion Schedule => new(null, FrequencyUnit.Day, 1, [], false, null, "07:00", [], []);

    [Fact]
    public void ValidSchedules_KeepOneTimeFixedAndFlexibleCadences()
    {
        HabitSetupSuggestionGuard.IsValid(Schedule).Should().BeTrue();
        HabitSetupSuggestionGuard.IsValid(Schedule with { FrequencyUnit = null, FrequencyQuantity = null }).Should().BeTrue();
        HabitSetupSuggestionGuard.IsValid(Schedule with { FrequencyUnit = FrequencyUnit.Week, IsFlexible = true, FlexibleTarget = 3 }).Should().BeTrue();
    }

    [Fact]
    public void InvalidScheduleData_IsRefused()
    {
        HabitSetupSuggestion[] invalid =
        [
            Schedule with { DueTime = "tomorrow" }, Schedule with { FrequencyQuantity = 0 },
            Schedule with { FrequencyUnit = (FrequencyUnit)99 }, Schedule with { FrequencyUnit = null },
            Schedule with { Days = [DayOfWeek.Monday, DayOfWeek.Monday] },
            Schedule with { FrequencyUnit = FrequencyUnit.Week, Days = [DayOfWeek.Monday] },
            Schedule with { SubHabits = ["Read"], ChecklistItems = ["Buy books"] },
            Schedule with { SubHabits = [new string('x', 101)] },
            Schedule with { ChecklistItems = ["Visit example.com"] },
            Schedule with { IsFlexible = true, FlexibleTarget = null },
            Schedule with { FlexibleTarget = 3 }, Schedule with { Emoji = "https://x.test" }
        ];
        invalid.Should().OnlyContain(value => !HabitSetupSuggestionGuard.IsValid(value));
    }
}
