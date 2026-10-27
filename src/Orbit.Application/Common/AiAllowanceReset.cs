using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Common;

public static class AiAllowanceReset
{
    public static DateTime GetResetsAtUtc(User user, DateOnly userToday)
    {
        var period = AiAllowancePeriod.For(userToday, user.HasProAccess, user.WeekStartDay);
        var zone = TimeZoneHelper.FindTimeZone(user.TimeZone, userId: user.Id);
        var midnight = period.End.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(midnight))
            midnight = midnight.AddMinutes(1);
        if (zone.IsAmbiguousTime(midnight))
            return new DateTimeOffset(midnight, zone.GetAmbiguousTimeOffsets(midnight).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(midnight, zone);
    }
}
