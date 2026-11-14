using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// The key that says two habit titles are one title: <see cref="Normalize"/>, so case, accents, punctuation and
    /// spaces do not count. A title with no letter or digit has its <see cref="SymbolText"/>, so different emoji stay
    /// two, and a title of only punctuation has its <see cref="PunctuationText"/>. Only a missing or blank title, which
    /// no habit has, gives the empty key.
    /// </summary>
    public static string TitleKey(string? title) =>
        title is null ? string.Empty
        : Normalize(title) is { Length: > 0 } normalized ? normalized
        : SymbolText(title) is { Length: > 0 } symbols ? symbols
        : PunctuationText(title);

    /// <summary>
    /// The message names the title. A title with words is found in the normalized message. A title with no letter or
    /// digit, such as one of emoji only, is found as whole emoji in the <see cref="SymbolText"/> of the message.
    /// Punctuation in a message only separates words, so a title of only punctuation is named by no message.
    /// </summary>
    public static bool MentionsTitle(string message, string title)
    {
        var words = Normalize(title);
        if (words.Length > 0)
            return Normalize(message).Contains(words, StringComparison.Ordinal);
        var symbols = SymbolText(title);
        return symbols.Length > 0 && Regex.IsMatch(SymbolText(message), WholeTokens(symbols), RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The message names the title right after a lead, a pattern over normalized words such as "under ". The title is
    /// found as <see cref="MentionsTitle"/> finds it, and no letter, digit or other emoji joins it. A title of only
    /// punctuation is named by no message.
    /// </summary>
    public static bool NamesTitleAfter(string message, string lead, string title)
    {
        var words = Normalize(title);
        if (words.Length > 0)
            return Regex.IsMatch(Normalize(message), lead + Regex.Escape(words) + @"(?!\w)", RegexOptions.None, TimeSpan.FromSeconds(1));
        var symbols = SymbolText(title);
        return symbols.Length > 0
            && Regex.IsMatch(SymbolText(message), lead + WholeTokens(symbols), RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static string WholeTokens(string symbols) => "(?<![^ ])" + Regex.Escape(symbols) + "(?![^ ])";

    /// <summary>
    /// The text as tokens one space apart: each word of letters and digits, folded as <see cref="Normalize"/> folds it,
    /// and each other text element (a whole emoji with its skin tone, joiners and keycap) as a token of its own. A
    /// variation selector or an accent does not count. Punctuation and spaces only separate the tokens, but an emoji in
    /// <see cref="PunctuationEmoji"/> is a token.
    /// </summary>
    private static string SymbolText(string value)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = new StringBuilder();
            foreach (var character in elements.GetTextElement().Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                    element.Append(char.ToLowerInvariant(character));
            var text = element.ToString();
            if (text.Length > 0 && text.All(char.IsLetterOrDigit))
            {
                word.Append(text);
                continue;
            }
            if (word.Length > 0)
                tokens.Add(word.ToString());
            word.Clear();
            if (!text.All(SeparatesTokens))
                tokens.Add(text);
        }
        if (word.Length > 0)
            tokens.Add(word.ToString());
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// The emoji that the Unicode emoji data puts in a punctuation category: ‼ ⁉ 〰 〽. Each is an emoji token, with or
    /// without a variation selector. "#" and "*" are not here, because they are emoji only in a keycap, which keeps its
    /// enclosing mark.
    /// </summary>
    private const string PunctuationEmoji = "‼⁉〰〽";

    private static bool SeparatesTokens(char character) =>
        char.IsWhiteSpace(character) || char.IsPunctuation(character) && !PunctuationEmoji.Contains(character);

    /// <summary>
    /// The text of a title of only punctuation, where the punctuation is all the title holds and thus counts. A variation
    /// selector or an accent does not count, and each run of spaces is one space. A blank text gives an empty text.
    /// </summary>
    private static string PunctuationText(string value)
    {
        var folded = value.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark);
        return string.Join(' ', string.Concat(folded).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
