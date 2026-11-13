using System.Text.RegularExpressions;

namespace Orbit.Application.Chat;

/// <summary>
/// Reads how a message scopes a set of same-titled records. A message that names the whole set, or members by
/// order, gives a write a clear target, so the write needs no question. A set or order word counts only when it
/// scopes the records: a word in a time phrase (every day, last week, todos os dias), in a filler phrase (one last
/// thing, that is all, tudo bem) or in the shared title (Top 3 Stretch) names no set.
/// </summary>
internal static partial class HabitSetRequestScope
{
    internal static bool NamesWholeSet(string message, string title) => WholeSet().IsMatch(ScopeText(message, title));

    internal static bool NamesMembersByOrder(string message, string title) =>
        OrderedMembers().IsMatch(ScopeText(message, title));

    /// <summary>
    /// The message without its time phrases, filler phrases and title. Each removed phrase leaves a break, so the
    /// words on its two sides cannot join into a new set or order phrase.
    /// </summary>
    private static string ScopeText(string message, string title)
    {
        var text = NotAboutTheSet().Replace(HabitNameMatcher.Normalize(message), Break);
        var normalizedTitle = HabitNameMatcher.Normalize(title);
        return normalizedTitle.Length == 0 ? text : Regex.Replace(text, @"\b" + Regex.Escape(normalizedTitle) + @"\b", Break,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private const string Break = " | ";
    private const string SetWord = @"(?:all|every|each|both|todos|todas|ambos|ambas|tudo)";
    private const string Quantity = @"(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten|um|uma|dois|duas|tres|quatro|cinco|seis|sete|oito|nove|dez)";
    private const string Order = @"(?:first|last|top|bottom|primeir[oa]s?|ultim[oa]s?)";
    private const string Joiner = @"(?:the|of|other|single|and|o|a|os|as|de|do|da|dos|das|no|na|nos|nas|e)";
    private const string TimeOrFillerNoun = @"(?:minutes?|hours?|days?|daily|weeks?|weekdays?|weekends?|months?|years?|mornings?|afternoons?|evenings?|nights?|times?|mondays?|tuesdays?|wednesdays?|thursdays?|fridays?|saturdays?|sundays?|things?|ways?"
        + @"|minutos?|horas?|dias?|semanas?|fim|fins|mes|meses|anos?|manhas?|tardes?|noites?|madrugadas?|vez|vezes|segundas?|tercas?|quartas?|quintas?|sextas?|sabados?|domingos?|feiras?|coisas?|formas?|maneiras?|jeitos?)";
    private const string FillerPhrase = @"(?:(?:is|s)\s+all|all\s+(?:right|good|in\s+all)|(?:first\s+of|at|after|above)\s+all|each\s+other"
        + @"|e\s+tudo|tudo\s+(?:bem|bom|certo|ok)|(?:acima|antes|apesar)\s+de\s+tudo)";

    [GeneratedRegex(@"\b(?:(?:" + SetWord + "|" + Quantity + "|" + Order + "|" + Joiner + @")\s+)+" + TimeOrFillerNoun + @"\b|\b" + FillerPhrase + @"\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex NotAboutTheSet();

    [GeneratedRegex(@"\b" + SetWord + @"\b|\b(?:habits|habitos)\s+(?:called|named|titled|chamados|chamadas|intitulados)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex WholeSet();

    [GeneratedRegex(@"\b" + Quantity + @"\s+(?:\w+\s+)?" + Order + @"\b|\b" + Order + @"\s+(?:\w+\s+)?" + Quantity + @"\b|\b(?:primeiros|primeiras|ultimos|ultimas)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex OrderedMembers();
}
