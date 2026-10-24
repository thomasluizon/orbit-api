using System.Text.RegularExpressions;

namespace Orbit.Application.Chat.Grounding;

public sealed record GroundingViolation(string Kind, string Claim);

/// <summary>Deterministic screening of numeric claims and identifiable entity references.</summary>
public static partial class GroundednessChecker
{
    public static IReadOnlyList<GroundingViolation> Check(
        string? text, TurnFactSet facts, IEnumerable<string>? knownEntityNames = null, string? language = null,
        string? userMessage = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var portuguese = language?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) == true;
        var allowed = facts.Facts.Select(fact => (fact.Kind, fact.Value)).ToHashSet();
        if (!string.IsNullOrWhiteSpace(userMessage))
            allowed.UnionWith(GroundingTokens.Read(userMessage, portuguese).Select(token => (token.Kind, token.Value)));
        var violations = new HashSet<GroundingViolation>();
        foreach (var token in GroundingTokens.Read(text, portuguese))
        {
            if (!allowed.Contains((token.Kind, token.Value)))
                violations.Add(new GroundingViolation(token.Kind, token.Text));
        }

        var normalizedText = GroundingTokens.NormalizeName(text);
        var candidates = (knownEntityNames ?? [])
            .Concat(GroundingTokens.QuotedNames().Matches(text).Select(match => match.Groups[1].Value))
            .Concat(NamedEntityPattern().Matches(text).Select(match => match.Groups[1].Value.Trim()));
        foreach (var name in candidates.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var normalized = GroundingTokens.NormalizeName(name);
            if (Regex.IsMatch(normalizedText, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(normalized)}(?![\p{{L}}\p{{N}}])", RegexOptions.None, TimeSpan.FromSeconds(1))
                && !allowed.Contains(("EntityName", normalized)))
            {
                violations.Add(new GroundingViolation("EntityName", normalized));
            }
        }
        return violations.ToArray();
    }

    [GeneratedRegex(@"\b(?:habit|goal|hábito|habito|meta)\s+['‘]([^'’\r\n]+)['’]", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex NamedEntityPattern();
}
