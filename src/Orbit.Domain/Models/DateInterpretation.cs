using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Orbit.Domain.Common;

namespace Orbit.Domain.Models;

public sealed record DateInterpretation(DateOnly? Date, DateOnly ReferenceDate);

public sealed partial record DateInterpretationInput
{
    public const int MaxPhraseLength = 200;
    public string Phrase { get; }
    public string Language { get; }

    private DateInterpretationInput(string phrase, string language)
    {
        Phrase = phrase;
        Language = language;
    }

    public static Result<DateInterpretationInput> Create(string? phrase, string? language)
    {
        if (string.IsNullOrWhiteSpace(phrase) || phrase.Length > MaxPhraseLength
            || phrase.Any(char.IsControl) || language is not ("en" or "pt-BR"))
            return Result.Failure<DateInterpretationInput>(DomainErrors.InvalidDatePhrase);
        return Result.Success(new DateInterpretationInput(phrase.Trim(), language));
    }

    public bool IsUnresolvable()
    {
        if (NumericDate().IsMatch(IsoDate().Replace(Phrase, "")) || Instructions().IsMatch(Phrase))
            return true;
        var explicitDates = new HashSet<(int Year, int Month, int Day)>();
        foreach (Match match in IsoDate().Matches(Phrase))
        {
            if (!TryParseDate(match.Value, out var date))
                return true;
            explicitDates.Add((date.Year, date.Month, date.Day));
        }

        var normalized = RemoveAccents(Phrase.ToLowerInvariant());
        var culture = CultureInfo.GetCultureInfo(Language);
        for (var month = 1; month <= 12; month++)
        {
            var name = RemoveAccents(culture.DateTimeFormat.GetMonthName(month).ToLowerInvariant());
            var abbreviation = RemoveAccents(culture.DateTimeFormat.GetAbbreviatedMonthName(month).ToLowerInvariant()).TrimEnd('.');
            var monthPattern = $"(?:{Regex.Escape(name)}|{Regex.Escape(abbreviation)}\\.?)";
            const string dayPattern = @"(?<day>\d{1,2})(?:st|nd|rd|th|º|°)?(?!\w)";
            var alternativeDays = $@"(?:\s+(?:or|ou)\s+{dayPattern})*";
            var daysPattern = $"{dayPattern}{alternativeDays}";
            var pattern = $@"(?:\b{daysPattern}\s+(?:(?:de|of)\s+)?{monthPattern}\b{alternativeDays}|\b{monthPattern}\s+(?:the\s+)?{daysPattern})(?:\s*(?:,|de)?\s*(?<year>[+-]?\d+)\b)?{alternativeDays}";
            foreach (Match match in Regex.Matches(normalized, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            {
                var year = 2000;
                if (match.Groups["year"].Success
                    && !int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out year))
                    return true;
                if (year is < 1 or > 9999)
                    return true;
                foreach (Capture capture in match.Groups["day"].Captures)
                {
                    var day = int.Parse(capture.Value, CultureInfo.InvariantCulture);
                    if (day < 1 || day > DateTime.DaysInMonth(year, month))
                        return true;
                    explicitDates.Add((match.Groups["year"].Success ? year : 0, month, day));
                }
            }
        }
        return explicitDates.Count > 1;
    }

    public static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        && date != DateOnly.MinValue
        && date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == value;

    private static string RemoveAccents(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark));

    [GeneratedRegex(@"\b\d{1,4}\s*[/.-]\s*\d{1,2}(?:\s*[/.-]\s*\d{1,4})?\b(?<!\d{4}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(ignore|instructions?|system|prompt|execute|delete|ignora\w*|instru\w*|apaga\w*|exclu\w*)\b|[{}<>;]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Instructions();
}
