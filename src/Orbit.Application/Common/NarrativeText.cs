using System.Text.RegularExpressions;

namespace Orbit.Application.Common;

public static class NarrativeText
{
    public static string WithoutTitles(string text, IEnumerable<string> literalNames)
    {
        var names = literalNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(name => name.Length).ToArray();
        return names.Length == 0 ? text : Regex.Replace(text, string.Join('|', names.Select(Regex.Escape)),
            " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

}
