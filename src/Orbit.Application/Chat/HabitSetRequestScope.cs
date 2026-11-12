using System.Text.RegularExpressions;

namespace Orbit.Application.Chat;

/// <summary>
/// Reads how a message scopes a set of same-titled records. A message that names the whole set, or members by
/// order, gives a write a clear target, so the write needs no question.
/// </summary>
internal static partial class HabitSetRequestScope
{
    internal static bool NamesWholeSet(string message) => WholeSet().IsMatch(HabitNameMatcher.Normalize(message));

    internal static bool NamesMembersByOrder(string message) =>
        OrderedMembers().IsMatch(HabitNameMatcher.Normalize(message));

    private const string Quantity = @"(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten|um|uma|dois|duas|tres|quatro|cinco|seis|sete|oito|nove|dez)";
    private const string Order = @"(?:first|last|top|bottom|primeir[oa]s?|ultim[oa]s?)";

    [GeneratedRegex(@"\b(?:all|every|each|both|todos|todas|ambos|ambas|tudo)\b|\b(?:habits|habitos)\s+(?:called|named|titled|chamados|chamadas|intitulados)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex WholeSet();

    [GeneratedRegex(@"\b" + Quantity + @"\s+(?:\w+\s+)?" + Order + @"\b|\b" + Order + @"\s+(?:\w+\s+)?" + Quantity + @"\b|\b(?:primeiros|primeiras|ultimos|ultimas)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex OrderedMembers();
}
