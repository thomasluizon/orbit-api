using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface ISummaryService
{
    Task<Result<string>> GenerateSummaryAsync(
        IEnumerable<Habit> allHabits,
        DateOnly dateFrom,
        DateOnly dateTo,
        bool includeOverdue,
        string language,
        TimeOnly? currentLocalTime,
        CancellationToken cancellationToken = default);
}
