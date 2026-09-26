using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class StreakFreeze : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly UsedOnDate { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public StreakFreezeOrigin? Origin { get; private set; }

    private StreakFreeze() { }

    public static Result<IReadOnlyList<StreakFreeze>> CreateGap(
        Guid userId, IReadOnlyCollection<DateOnly>? dates, DateOnly userToday)
    {
        if (userId == Guid.Empty || dates is null || dates.Count == 0)
            return Result.Failure<IReadOnlyList<StreakFreeze>>(DomainErrors.InvalidStreakGap);

        var ordered = dates.Order().ToArray();
        if (ordered[0] == DateOnly.MinValue
            || ordered[^1].DayNumber != userToday.DayNumber - 1
            || ordered.Where((date, index) => index > 0 && date == ordered[index - 1]).Any())
        {
            return Result.Failure<IReadOnlyList<StreakFreeze>>(DomainErrors.InvalidStreakGap);
        }

        return Result.Success<IReadOnlyList<StreakFreeze>>(
            ordered.Select(date => Create(userId, date, StreakFreezeOrigin.Manual)).ToArray());
    }

    public static StreakFreeze Create(Guid userId, DateOnly date, StreakFreezeOrigin origin)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User ID is required.", nameof(userId));
        if (date == DateOnly.MinValue)
            throw new ArgumentOutOfRangeException(nameof(date), "Used date is required.");
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), "Freeze origin is invalid.");

        return new StreakFreeze
        {
            UserId = userId,
            UsedOnDate = date,
            Origin = origin,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
