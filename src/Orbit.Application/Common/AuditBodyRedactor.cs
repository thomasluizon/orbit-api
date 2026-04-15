using System.Text.RegularExpressions;

namespace Orbit.Application.Common;

/// <summary>
/// Redacts sensitive substrings from raw request/response bodies before they
/// are persisted to the agent audit log. Targets bearer tokens, API keys,
/// email addresses, GUIDs, and obvious "secret"/"password"/"token" key/value
/// pairs in JSON. Output is bounded by <paramref name="maxLength"/>.
/// </summary>
public static partial class AuditBodyRedactor
{
    private const string Placeholder = "[REDACTED]";

    public static string Redact(string? body, int maxLength = 1000)
    {
        if (string.IsNullOrEmpty(body))
            return string.Empty;

        var redacted = body;

        redacted = BearerTokenRegex().Replace(redacted, $"Bearer {Placeholder}");
        redacted = ApiKeyRegex().Replace(redacted, Placeholder);
        redacted = EmailRegex().Replace(redacted, Placeholder);
        redacted = GuidRegex().Replace(redacted, Placeholder);
        redacted = JsonSecretRegex().Replace(redacted, m =>
            $"\"{m.Groups[1].Value}\":\"{Placeholder}\"");

        if (redacted.Length > maxLength)
            redacted = redacted[..Math.Max(0, maxLength)];

        return redacted;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-+/=]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"orb_[A-Za-z0-9_\-]{16,}")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex("\"(password|secret|token|api[_-]?key|apiKey|access[_-]?token|refresh[_-]?token|client[_-]?secret)\"\\s*:\\s*\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretRegex();
}
