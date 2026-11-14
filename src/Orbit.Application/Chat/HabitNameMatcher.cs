using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat;

public static class HabitNameMatcher
{
    public static string Normalize(string value) => Fold(value, keepSymbols: false);

    /// <summary>
    /// The key that says two habit titles are one title. It is <see cref="Normalize"/>, so case, accents, punctuation and
    /// spaces do not count. A title with no letter or digit, such as one of emoji only, normalizes to nothing, so its key
    /// keeps its symbols and two different such titles stay two. A missing title gives an empty key.
    /// </summary>
    public static string TitleKey(string? title) =>
        title is null ? string.Empty
        : Normalize(title) is { Length: > 0 } normalized ? normalized : Fold(title, keepSymbols: true);

    /// <summary>
    /// The message names the title. A title with words is found in the normalized message. A title with no letter or
    /// digit, such as one of emoji only, is found by its symbols in the message folded the same way. A title with no
    /// key is named by no message.
    /// </summary>
    public static bool MentionsTitle(string message, string title)
    {
        var words = Normalize(title);
        if (words.Length > 0)
            return Normalize(message).Contains(words, StringComparison.Ordinal);
        var symbols = TitleKey(title);
        return symbols.Length > 0 && Fold(message, keepSymbols: true).Contains(symbols, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message names the title right after a lead, a pattern over normalized words such as "under ". The title is
    /// found as <see cref="MentionsTitle"/> finds it, and no letter or digit follows it. A title with no key is named by
    /// no message.
    /// </summary>
    public static bool NamesTitleAfter(string message, string lead, string title)
    {
        var words = Normalize(title);
        var key = words.Length > 0 ? words : TitleKey(title);
        return key.Length > 0 && Regex.IsMatch(words.Length > 0 ? Normalize(message) : Fold(message, keepSymbols: true),
            lead + Regex.Escape(key) + @"(?!\w)", RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static string Fold(string value, bool keepSymbols)
    {
        var result = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            result.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character)
                : keepSymbols && !char.IsWhiteSpace(character) ? character : ' ');
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
        var exact = candidates.Where(habit => TitleKey(habit.Title) == TitleKey(name)).ToList();
        if (exact.Count > 0)
            return exact.Count == 1 ? exact[0] : null;
        var matches = candidates.Where(habit => Matches(habit.Title, name)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
