using Orbit.Domain.Entities;

namespace Orbit.Application.Habits.Services;

public static class SiblingTitleDisambiguator
{
    /// <summary>
    /// Maps each habit that shares its exact title with another sibling to a positional suffix
    /// (" (1 of 3)"), so identical repeated habits render as distinct, countable lines a language
    /// model cannot silently collapse into one. Habits whose title is unique among the siblings are
    /// absent from the result. Input order defines the numbering.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> ComputeSuffixes(IReadOnlyList<Habit> orderedSiblings)
    {
        var totals = orderedSiblings
            .GroupBy(habit => habit.Title, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var suffixes = new Dictionary<Guid, string>();
        if (totals.Count == 0)
            return suffixes;

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var habit in orderedSiblings)
        {
            if (!totals.TryGetValue(habit.Title, out var total))
                continue;

            var position = seen.TryGetValue(habit.Title, out var previous) ? previous + 1 : 1;
            seen[habit.Title] = position;
            suffixes[habit.Id] = $" ({position} of {total})";
        }

        return suffixes;
    }
}
