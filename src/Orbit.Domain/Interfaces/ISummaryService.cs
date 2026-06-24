using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface ISummaryService
{
    Task<Result<string>> GenerateSummaryAsync(
        IEnumerable<Habit> allHabits,
        DateOnly dateFrom,
        DateOnly dateTo,
        DateOnly userToday,
        string language,
        TimeOnly? currentLocalTime,
        int currentStreak,
        int streakFreezesAccumulated,
        CancellationToken cancellationToken = default);
}
