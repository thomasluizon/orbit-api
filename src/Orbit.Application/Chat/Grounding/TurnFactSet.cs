using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Grounding;

public sealed record GroundingFact(string Kind, string Value, string ToolName, string ToolCallId);

/// <summary>Facts returned by successful tools in one turn, with provenance for evaluations.</summary>
public sealed record TurnFactSet(IReadOnlyList<GroundingFact> Facts)
{
    public static TurnFactSet Empty { get; } = new(Array.Empty<GroundingFact>());

    public static TurnFactSet FromToolResults(IEnumerable<AiToolCallResult> results)
    {
        var facts = new HashSet<GroundingFact>();
        foreach (var result in results.Where(result => result.Success))
        {
            AddText(result.EntityName, result.EntityId is not null, result, facts);
            if (result.Payload is not null)
                ReadPayload(JsonSerializer.SerializeToElement(result.Payload), null, result, facts);
        }
        return new TurnFactSet(Array.AsReadOnly(facts.ToArray()));
    }

    private static void ReadPayload(JsonElement element, string? property, AiToolCallResult result, HashSet<GroundingFact> facts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var child in element.EnumerateObject())
                    ReadPayload(child.Value, child.Name, result, facts);
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    ReadPayload(child, property, result, facts);
                break;
            case JsonValueKind.Number:
                AddText(element.GetRawText(), false, result, facts);
                break;
            case JsonValueKind.String:
                var key = property?.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
                if (key is not null && (key == "ID" || key.EndsWith("ID", StringComparison.Ordinal)))
                    break;
                AddText(element.GetString(), key is "TITLE" or "NAME" or "HABITTITLE" or "GOALTITLE" or "HABITNAME" or "GOALNAME" or "TAGNAME", result, facts);
                break;
        }
    }

    private static void AddText(string? text, bool entityName, AiToolCallResult result, HashSet<GroundingFact> facts)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (var token in GroundingTokens.Read(text, portuguese: false))
            facts.Add(new GroundingFact(token.Kind, token.Value, result.Name, result.Id));

        if (entityName)
            facts.Add(new GroundingFact("EntityName", GroundingTokens.NormalizeName(text), result.Name, result.Id));

        foreach (System.Text.RegularExpressions.Match match in GroundingTokens.QuotedNames().Matches(text))
            facts.Add(new GroundingFact("EntityName", GroundingTokens.NormalizeName(match.Groups[1].Value), result.Name, result.Id));
    }
}
