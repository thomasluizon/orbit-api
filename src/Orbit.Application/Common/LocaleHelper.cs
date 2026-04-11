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
}
