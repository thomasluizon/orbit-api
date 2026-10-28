using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbit.Application.Chat;

internal static partial class ModelAnswerText
{
    internal static string? Extract(string? text) => Extract(text, 0);

    private static string? Extract(string? text, int depth)
    {
        if (text is null || depth >= 4) return null;
        var candidate = text.Trim();
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = candidate.IndexOf('\n');
            if (newline < 0) return text;
            var format = candidate[3..newline].Trim();
            if (format.Length > 0 && !format.Equals("json", StringComparison.OrdinalIgnoreCase)) return text;
            candidate = candidate[(newline + 1)..].Trim();
            if (candidate.EndsWith("```", StringComparison.Ordinal)) candidate = candidate[..^3].TrimEnd();
        }
        if (!candidate.StartsWith('{')) return text;
        try
        {
            using var document = JsonDocument.Parse(candidate);
            var root = document.RootElement;
            if (root.TryGetProperty("parts", out var parts))
            {
                if (parts.ValueKind != JsonValueKind.Array) return null;
                var narrative = parts.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "text")
                    .Select(part => part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String
                        ? Extract(value.GetString(), depth + 1) : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value));
                var visible = string.Join("\n", narrative);
                return visible.Length == 0 ? null : visible;
            }
            if (root.TryGetProperty("aiMessage", out var message))
                return message.ValueKind == JsonValueKind.String ? Extract(message.GetString(), depth + 1) : null;
            return text;
        }
        catch (JsonException)
        {
            return EnvelopePrefix().IsMatch(candidate) ? null : text;
        }
    }

    [GeneratedRegex("^\\{\\s*\"(?:parts|aiMessage)\"\\s*:")]
    private static partial Regex EnvelopePrefix();
}
