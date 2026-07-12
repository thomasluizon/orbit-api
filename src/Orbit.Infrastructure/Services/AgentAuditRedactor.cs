using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Masks the values of sensitive JSON properties (verification codes, tokens,
/// passwords, secrets, user names, marketing broadcast bodies) before an argument
/// payload is persisted to the agent audit trail, so neither a secret nor personal
/// data can land in <c>AgentAuditLogs.RedactedArguments</c>. Both the structured
/// agent-operation path and the legacy MCP audit path route through this. A body that
/// does not parse as JSON is masked whole rather than stored raw.
/// </summary>
public static class AgentAuditRedactor
{
    private const int MaxLength = 1000;
    private const string Mask = "***REDACTED***";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.Ordinal)
    {
        "code", "codeverifier", "verifier", "authorization", "auth", "otp", "pin",
        "name", "firstname", "lastname", "fullname", "displayname", "username", "nickname",
    };

    private static readonly string[] SensitiveFragments =
    [
        "token", "password", "secret", "credential", "apikey", "bodyhtml",
    ];

    /// <summary>Redacts sensitive fields from a JSON argument payload, then truncates to the audit cap.</summary>
    public static string? Redact(string? rawArguments)
    {
        if (string.IsNullOrEmpty(rawArguments))
            return rawArguments;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(rawArguments);
        }
        catch (JsonException)
        {
            return Mask;
        }

        if (node is null)
            return Truncate(rawArguments);

        RedactNode(node);
        return Truncate(node.ToJsonString());
    }

    private static void RedactNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    if (IsSensitive(key))
                        obj[key] = Mask;
                    else if (obj[key] is { } child)
                        RedactNode(child);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                        RedactNode(item);
                }
                break;
        }
    }

    private static bool IsSensitive(string key)
    {
        var normalized = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return SensitiveKeys.Contains(normalized)
            || Array.Exists(SensitiveFragments, normalized.Contains);
    }

    private static string Truncate(string value) =>
        value.Length <= MaxLength ? value : value[..MaxLength];
}
