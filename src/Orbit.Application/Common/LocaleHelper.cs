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

    /// <summary>
    /// Returns true if an <c>Accept-Language</c> header prefers Portuguese.
    /// <para>
    /// The header is a weighted list, so the whole value is not a language code:
    /// <c>pt;q=0.1,en;q=0.9</c> prefers English while starting with <c>pt</c>. Tags are ranked by
    /// their quality value, which defaults to 1 when absent, and ties keep the order the client
    /// sent. A tag with <c>q=0</c> is a refusal and is dropped.
    /// </para>
    /// </summary>
    public static bool IsPortugueseAcceptLanguage(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return false;

        var best = default(string);
        var bestQuality = 0d;
        var position = 0;
        var bestPosition = 0;

        foreach (var entry in acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                continue;

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

            position++;
            if (quality <= 0)
                continue;

            if (best is null || quality > bestQuality || (quality == bestQuality && position < bestPosition))
            {
                best = parts[0];
                bestQuality = quality;
                bestPosition = position;
            }
        }

        return IsPortuguese(best);
    }
}
