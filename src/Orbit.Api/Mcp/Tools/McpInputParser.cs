using System.Globalization;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// Safe parsers for MCP tool inputs. AI-supplied strings cannot crash the request — they must
/// surface as friendly tool errors so the model can self-correct.
/// </summary>
internal static class McpInputParser
{
    public static Guid ParseGuid(string value, string fieldName)
    {
        if (!Guid.TryParse(value, out var parsed))
            throw new ArgumentException($"Invalid {fieldName}: '{value}' is not a valid GUID.");
        return parsed;
    }

    public static Guid? ParseOptionalGuid(string? value, string fieldName)
        => value is null ? null : ParseGuid(value, fieldName);

    public static DateOnly ParseDate(string value, string fieldName)
    {
        if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new ArgumentException($"Invalid {fieldName}: '{value}' is not a valid date (expected YYYY-MM-DD).");
        return parsed;
    }

    public static DateOnly? ParseOptionalDate(string? value, string fieldName)
        => value is null ? null : ParseDate(value, fieldName);

    public static TimeOnly ParseTime(string value, string fieldName)
    {
        if (!TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new ArgumentException($"Invalid {fieldName}: '{value}' is not a valid time (expected HH:MM or HH:MM:SS).");
        return parsed;
    }

    public static TimeOnly? ParseOptionalTime(string? value, string fieldName)
        => value is null ? null : ParseTime(value, fieldName);
}
