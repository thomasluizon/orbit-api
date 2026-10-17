namespace Orbit.Application.Common;

/// <summary>
/// Centralizes language/locale resolution used across AI services and notification services.
/// </summary>
public static class LocaleHelper
{
    /// <summary>
    /// Returns the AI-friendly language name for a given language code.
    /// </summary>
    public static string GetAiLanguageName(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "English";

        return language.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "Brazilian Portuguese",
            _ => "English"
        };
    }

    /// <summary>
    /// Returns true if the language code represents Portuguese.
    /// </summary>
    public static bool IsPortuguese(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return false;

        return language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPortugueseAcceptLanguage(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return false;

        var best = default(string);
        var bestQuality = 0d;

        foreach (var entry in acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                continue;

            var quality = ParseQuality(parts);
            if (quality <= 0)
                continue;

            if (best is null || quality > bestQuality)
            {
                best = parts[0];
                bestQuality = quality;
            }
        }

        return IsPortuguese(best);
    }

    private static double ParseQuality(string[] parts)
    {
        var quality = 1d;
        foreach (var parameter in parts.Skip(1))
        {
            if (!parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                continue;

            if (double.TryParse(
                    parameter[2..],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                quality = parsed;
            }
        }

        return quality;
    }
}
