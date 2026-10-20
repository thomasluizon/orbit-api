using System.Globalization;
using System.Text;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat;

public static class HabitNameMatcher
{
    public static string Normalize(string value)
    {
        var result = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            result.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool Matches(string title, string search)
    {
        var normalized = Normalize(search);
        if (normalized.Length == 0)
            return false;
        var titleWords = Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return normalized.Split(' ').All(word => titleWords.Any(candidate => candidate.StartsWith(word, StringComparison.Ordinal)));
    }

    public static Habit? Resolve(IEnumerable<Habit> habits, string name)
    {
        var candidates = habits.ToList();
        var exact = candidates.Where(habit => Normalize(habit.Title) == Normalize(name)).ToList();
        if (exact.Count > 0)
            return exact.Count == 1 ? exact[0] : null;
        var matches = candidates.Where(habit => Matches(habit.Title, name)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

}
