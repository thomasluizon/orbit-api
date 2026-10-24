using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Application.Habits.Commands;

public static class LegacyHabitSchedule
{
    public static Result<HabitSchedule> Resolve(bool isGeneral, bool isFlexible,
        FrequencyUnit? frequencyUnit, int? frequencyQuantity, bool isBadHabit = false,
        IReadOnlyList<DayOfWeek>? days = null, int? intervalWeeks = null)
    {
        if (isGeneral && (frequencyUnit is not null || frequencyQuantity is not null))
            return Result.Failure<HabitSchedule>(DomainErrors.GeneralHabitHasFrequency);
        if (isGeneral && isBadHabit)
            return Result.Failure<HabitSchedule>(DomainErrors.GeneralHabitIsBadHabit);
        if (frequencyUnit is not null && frequencyQuantity is null || frequencyQuantity is <= 0)
            return Result.Failure<HabitSchedule>(DomainErrors.FrequencyQuantityInvalid);
        if (intervalWeeks is < 1 or > DomainConstants.MaxIntervalWeeks)
            return Result.Failure<HabitSchedule>(DomainErrors.IntervalWeeksInvalid);
        if (isFlexible && frequencyUnit is null)
            return Result.Failure<HabitSchedule>(DomainErrors.FlexibleNeedsFrequencyUnit);
        if (isFlexible && days?.Count > 0)
            return Result.Failure<HabitSchedule>(DomainErrors.FlexibleHasDays);
        if (!isFlexible && days?.Count > 0 && (frequencyQuantity != 1 || frequencyUnit != FrequencyUnit.Day))
            return Result.Failure<HabitSchedule>(DomainErrors.DaysRequireQuantityOne);

        return Result.Success(isGeneral ? HabitSchedule.General
            : isFlexible ? HabitSchedule.Flexible
            : frequencyUnit is null ? HabitSchedule.OneTime : HabitSchedule.Recurring);
    }
}
