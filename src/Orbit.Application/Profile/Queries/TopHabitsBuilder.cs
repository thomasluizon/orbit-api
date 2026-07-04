using Orbit.Domain.Entities;

namespace Orbit.Application.Profile.Queries;

/// <summary>
/// A user's habit ranked by lifetime completions. Shared by the public profile and friend profile
/// projections so both surfaces render the same habit shape (title, emoji, completion count).
/// </summary>
public record TopHabit(string Title, string? Emoji, int CompletionCount);

/// <summary>
/// Ranks a user's top-level, non-bad habits by lifetime completions (logs with a positive value),
/// tie-broken by title, and returns the top three. Operates on already-loaded habits whose completed
/// logs are included, so callers control how the habits are fetched.
/// </summary>
public static class TopHabitsBuilder
{
    private const int TopHabitsCount = 3;

    public static IReadOnlyList<TopHabit> Build(IEnumerable<Habit> habits) =>
        habits
            .Where(h => h.ParentHabitId is null && !h.IsBadHabit)
            .Select(h => new TopHabit(h.Title, h.Emoji, h.Logs.Count(l => l.Value > 0)))
            .Where(h => h.CompletionCount > 0)
            .OrderByDescending(h => h.CompletionCount)
            .ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
            .Take(TopHabitsCount)
            .ToList();
}
