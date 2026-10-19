using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Application.Chat.Grounding;

internal static partial class GroundingTokens
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "d/M/yyyy", "d 'de' MMMM 'de' yyyy", "d MMMM yyyy", "MMMM d, yyyy", "MMMM d yyyy", "d MMM yyyy", "MMM d, yyyy"];

    public static string NormalizeName(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark))
        .Normalize(NormalizationForm.FormC).ToUpperInvariant().Trim();

    public static IEnumerable<(string Kind, string Value, string Text)> Read(string text, bool portuguese)
    {
        text = GuidPattern().Replace(text, " ");
        text = ListMarkerPattern().Replace(text, " ");
        var dates = DatePattern().Matches(text);
        foreach (Match match in dates)
        {
            if (TryDate(match.Value, out var date))
                yield return ("Date", date, match.Value);
        }

        text = DatePattern().Replace(text, match => TryDate(match.Value, out _) ? " " : match.Value);
        foreach (Match match in NumberPattern().Matches(text))
        {
            var raw = match.Value;
            var culture = portuguese ? Portuguese : CultureInfo.InvariantCulture;
            if (decimal.TryParse(raw, NumberStyles.Number, culture, out var number))
                yield return ("Number", number.ToString("G29", CultureInfo.InvariantCulture), raw);
        }
    }

    private static bool TryDate(string text, out string normalized)
    {
        foreach (var culture in new[] { Portuguese, CultureInfo.InvariantCulture })
        {
            if (DateOnly.TryParseExact(text, DateFormats, culture, DateTimeStyles.AllowWhiteSpaces, out var date))
            {
                normalized = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return true;
            }
        }
        normalized = string.Empty;
        return false;
    }

    [GeneratedRegex(@"\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^\s*\d+[.)]\s+", RegexOptions.Multiline, 1000)]
    private static partial Regex ListMarkerPattern();

    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4}|\d{1,2}(?: de)? [\p{L}]+(?: de)? \d{4}|[\p{L}]+ \d{1,2},? \d{4})\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?<![\p{L}\d])[-+]?\d+(?:[.,]\d+)*(?!\d)", RegexOptions.None, 1000)]
    private static partial Regex NumberPattern();

    [GeneratedRegex("[\"“]([^\"”\\r\\n]+)[\"”]", RegexOptions.None, 1000)]
    public static partial Regex QuotedNames();
}
