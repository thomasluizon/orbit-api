using System.Text;

namespace Orbit.Infrastructure.Services.Prompts;

internal static class PromptDataSanitizer
{
    public static string QuoteInline(string? value, int maxLength = 200)
    {
        var sanitized = SanitizeInline(value, maxLength)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return $"\"{sanitized}\"";
    }

    public static string SanitizeInline(string? value, int maxLength = 200) =>
        Sanitize(value, maxLength, preserveLineBreaks: false);

    public static string SanitizeBlock(string? value, int maxLength = 2000) =>
        Sanitize(value, maxLength, preserveLineBreaks: true);

    private static string Sanitize(string? value, int maxLength, bool preserveLineBreaks)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var sb = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        var previousWasNewline = false;

        foreach (var ch in normalized)
        {
            if (ShouldSkipCharacter(ch))
                continue;

            if (ch == '\n')
            {
                AppendNewline(sb, preserveLineBreaks, ref previousWasSpace, ref previousWasNewline);
                continue;
            }

            var current = ch == '\t' ? ' ' : ch;
            if (char.IsWhiteSpace(current))
            {
                AppendSingleSpace(sb, ref previousWasSpace, ref previousWasNewline);
                continue;
            }

            sb.Append(current);
            previousWasSpace = false;
            previousWasNewline = false;
        }

        var sanitized = sb.ToString().Trim();
        if (sanitized.Length == 0)
            return "(empty)";

        if (sanitized.Length <= maxLength)
            return sanitized;

        return sanitized[..Math.Max(0, maxLength - 3)].TrimEnd() + "...";
    }

    private static bool ShouldSkipCharacter(char ch)
    {
        return char.IsControl(ch) && ch is not '\n' and not '\t';
    }

    private static void AppendNewline(
        StringBuilder sb,
        bool preserveLineBreaks,
        ref bool previousWasSpace,
        ref bool previousWasNewline)
    {
        if (!preserveLineBreaks)
        {
            AppendSingleSpace(sb, ref previousWasSpace, ref previousWasNewline);
            return;
        }

        if (previousWasNewline)
            return;

        sb.Append('\n');
        previousWasNewline = true;
        previousWasSpace = false;
    }

    private static void AppendSingleSpace(
        StringBuilder sb,
        ref bool previousWasSpace,
        ref bool previousWasNewline)
    {
        if (previousWasSpace || previousWasNewline)
            return;

        sb.Append(' ');
        previousWasSpace = true;
    }
}
