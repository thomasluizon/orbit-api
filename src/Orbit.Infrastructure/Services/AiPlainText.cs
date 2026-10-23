using System.Globalization;
using System.Text.RegularExpressions;

namespace Orbit.Infrastructure.Services;

internal static partial class AiPlainText
{
    internal static string Normalize(string text, IEnumerable<string>? literalNames = null)
    {
        var names = literalNames?.Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal).OrderByDescending(name => name.Length).ToArray() ?? [];
        var protectedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = "\uE000";
        while (text.Contains(prefix, StringComparison.Ordinal))
            prefix += "\uE000";

        if (names.Length > 0)
            text = Regex.Replace(text, string.Join('|', names.Select(Regex.Escape)), match =>
            {
                var token = prefix + protectedNames.Count.ToString(CultureInfo.InvariantCulture) + "\uE001";
                protectedNames.Add(token, match.Value);
                return token;
            }, RegexOptions.None, TimeSpan.FromSeconds(1));

        text = AiSummaryService.StripMarkdownFences(text);
        text = LineMarkers().Replace(text, string.Empty);
        text = Links().Replace(text, "$1");
        string previous;
        do
        {
            previous = text;
            text = Emphasis().Replace(text, "$2");
        } while (text != previous);

        foreach (var (token, name) in protectedNames)
            text = text.Replace(token, name, StringComparison.Ordinal);
        return text.Trim();
    }

    [GeneratedRegex(@"(?m)^[\t ]*(?:#{1,6}[\t ]+|>[\t ]*|[-+*][\t ]+|\d+[.)][\t ]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LineMarkers();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\([^\)\r\n]*\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Links();

    [GeneratedRegex(@"(?<![\p{L}\p{N}\\])([*_]{1,3}|~~|`)(?!\s)([^\r\n]*?\S)\1(?![\p{L}\p{N}])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Emphasis();
}
