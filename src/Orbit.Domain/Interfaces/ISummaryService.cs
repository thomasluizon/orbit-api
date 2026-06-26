using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// One daily-summary generation: the warm narrative <paramref name="Summary"/> shown in the
/// Astra card, plus a short contextual <paramref name="Insight"/> nudge (may be empty) rendered
/// as the Today insight chip. Both come from a single AI call.
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
