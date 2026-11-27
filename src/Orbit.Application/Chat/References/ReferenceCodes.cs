using Orbit.Application.Habits.Services;

namespace Orbit.Application.Chat.References;

/// <summary>The outcome of one reference: the record it names, or the closest records of the type when it names none.</summary>
public sealed record ReferenceResolution(ReferenceRecord? Record, IReadOnlyList<ReferenceRecord> Closest)
{
    public bool IsResolved => Record is not null;
}

/// <summary>
/// The reference rules (thomasluizon/orbit-tickets#2129, plan 5.2 part 1). A reference is the first 8 hexadecimal digits
/// of a GUID, or 12 when two records of one type share the first 8.
/// </summary>
public static class ReferenceCodes
{
    public const int ShortLength = 8;
    public const int CollisionLength = 12;
    private const int ClosestCount = 3;

    public static string Hex(Guid id) => id.ToString("N");

    /// <summary>The length of the reference of each record of one type.</summary>
    public static IReadOnlyDictionary<Guid, int> Lengths(IEnumerable<ReferenceRecord> records) => records
        .DistinctBy(record => record.Id)
        .GroupBy(record => Hex(record.Id)[..ShortLength], StringComparer.Ordinal)
        .SelectMany(group => group.Select(record => (record.Id, Length: group.Count() > 1 ? CollisionLength : ShortLength)))
        .ToDictionary(pair => pair.Id, pair => pair.Length);

    /// <summary>
    /// Resolves the text against the person's records of one type. A full GUID resolves only as itself. Otherwise the
    /// first 8 hexadecimal digits of the text, without dashes, select the records; one match is the record and the rest
    /// of the text is ignored, and several matches use the next digits of the text to select one.
    /// </summary>
    public static ReferenceResolution Resolve(IReadOnlyList<ReferenceRecord> records, string text)
    {
        var trimmed = text.Trim();
        if (Guid.TryParse(trimmed, out var full))
            return records.FirstOrDefault(record => record.Id == full) is { } exact
                ? new ReferenceResolution(exact, [])
                : new ReferenceResolution(null, Closest(records, Digits(trimmed)));
        var digits = Digits(trimmed);
        if (digits.Length < ShortLength)
            return new ReferenceResolution(null, Closest(records, digits));
        var matches = records.Where(record => Hex(record.Id).StartsWith(digits[..ShortLength], StringComparison.Ordinal)).ToArray();
        for (var length = ShortLength + 1; matches.Length > 1 && length <= digits.Length; length++)
        {
            var narrowed = matches.Where(record => Hex(record.Id).StartsWith(digits[..length], StringComparison.Ordinal)).ToArray();
            if (narrowed.Length == 0)
                break;
            matches = narrowed;
        }
        return matches.Length == 1
            ? new ReferenceResolution(matches[0], [])
            : new ReferenceResolution(null, Closest(records, digits));
    }

    /// <summary>
    /// The text is a full GUID or starts with 8 hexadecimal digits. Other text, such as a habit name that
    /// <c>bulk_log_habits</c> accepts in <c>habit_ids</c>, is no reference and reaches the tool unchanged.
    /// </summary>
    public static bool IsReference(string text) => Guid.TryParse(text.Trim(), out _) || Digits(text).Length >= ShortLength;

    /// <summary>The leading hexadecimal digits of the text, in lower case, without dashes.</summary>
    public static string Digits(string text)
    {
        var digits = new System.Text.StringBuilder(text.Length);
        foreach (var character in text.Trim())
        {
            if (character == '-')
                continue;
            if (!Uri.IsHexDigit(character))
                break;
            digits.Append(char.ToLowerInvariant(character));
        }
        return digits.ToString();
    }

    /// <summary>
    /// The records of the type that are closest to the digits: the longest shared prefix first, then the fewest edits of
    /// the first 8 digits, then the title.
    /// </summary>
    private static ReferenceRecord[] Closest(IReadOnlyList<ReferenceRecord> records, string digits)
    {
        var head = digits.Length > ShortLength ? digits[..ShortLength] : digits;
        return records
            .OrderByDescending(record => SharedPrefix(Hex(record.Id), digits))
            .ThenBy(record => FuzzyMatcher.LevenshteinDistance(Hex(record.Id)[..ShortLength], head))
            .ThenBy(record => record.Title, StringComparer.Ordinal)
            .ThenBy(record => record.Id)
            .Take(ClosestCount)
            .ToArray();
    }

    private static int SharedPrefix(string left, string right)
    {
        var length = 0;
        while (length < left.Length && length < right.Length && left[length] == right[length]) length++;
        return length;
    }
}
