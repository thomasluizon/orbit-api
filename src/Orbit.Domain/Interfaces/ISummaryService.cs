using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// One daily-summary generation: the warm narrative <paramref name="Summary"/> shown in the
/// Astra card. <paramref name="Insight"/> is retained on the contract only for legacy mobile
/// clients (pre-July-2026 builds) that still parse and render a nudge chip; it is always populated
/// empty and no longer generated.
/// </summary>
public record DailySummaryContent(string Summary, string Insight);

public interface ISummaryService
{
    Task<Result<DailySummaryContent>> GenerateSummaryAsync(
        IEnumerable<Habit> allHabits,
        DateOnly dateFrom,
        DateOnly dateTo,
        DateOnly userToday,
        string language,
        TimeOnly? currentLocalTime,
        int currentStreak,
        int streakFreezesAccumulated,
        IReadOnlyDictionary<Guid, DateOnly> lastBadHabitSlipDates,
        CancellationToken cancellationToken = default);
}
