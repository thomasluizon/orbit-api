using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public sealed record HabitRankingReadRow(Guid HabitId, string Name, string? Emoji,
    Guid? ParentHabitId, int CompletedCount);

public sealed record HabitRankingPage(IReadOnlyList<HabitRankingReadRow> Rows,
    int EligibleCount, int TotalCount, string Fingerprint);

public interface IHabitRankingReader
{
    Task<HabitRankingPage> ReadAsync(HabitRankingDefinition query, int offset,
        CancellationToken cancellationToken = default);
}
