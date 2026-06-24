using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Orbit.Application.Chat;

/// <summary>
/// Process-wide response cache for a small curated set of static, general feature-FAQ questions
/// ("how do streaks work", "what is a streak freeze", …) in English and Portuguese. The cached value
/// is a real prior model answer (so it stays conversational and localized), keyed by FAQ + locale, so
/// the second user to ask a general feature question is served instantly without a model round-trip.
/// Only general feature questions are matched — never anything referencing a user's own data — and only
/// pure-text answers from tool-free turns are stored, so a cached answer is safe to share across users.
/// The key space is bounded by the curated FAQ set times the supported locales.
/// </summary>
public static class ChatFaqCache
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    private static readonly IReadOnlyList<(string Key, string[] Phrases)> Faqs =
    [
        ("streak_freeze", ["streak freeze", "how do freezes work", "what is a freeze", "what is a streak freeze",
            "congelamento de sequencia", "como funciona o congelamento", "o que e um freeze"]),
        ("streaks", ["how do streaks work", "how does the streak", "what is a streak", "what counts as a streak",
            "como funciona a sequencia", "como funcionam as sequencias", "o que e uma sequencia", "o que e streak"]),
        ("xp_levels", ["how does xp work", "how do levels work", "what is xp", "how does leveling work",
            "como funciona o xp", "como funcionam os niveis", "o que e xp", "como subir de nivel"]),
        ("free_vs_pro", ["free vs pro", "what does pro include", "what are the pro features", "difference between free and pro",
            "o que o pro inclui", "quais sao os recursos pro", "diferenca entre free e pro", "o que vem no pro"]),
        ("bad_habits", ["how do bad habits work", "what is a bad habit", "how does tracking a bad habit work",
            "como funcionam os habitos ruins", "o que e um habito ruim", "o que e um mau habito"]),
    ];

    /// <summary>
    /// Returns the curated FAQ key when the message is one of the recognised general feature questions,
    /// or null otherwise. Conservative: the message must contain a known FAQ phrase.
    /// </summary>
    public static string? TryMatchFaqKey(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var normalized = Normalize(message);
        foreach (var (key, phrases) in Faqs)
        {
            if (phrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal)))
                return key;
        }

        return null;
    }

    public static bool TryGetAnswer(string faqKey, string locale, out string answer) =>
        Cache.TryGetValue(BuildKey(faqKey, locale), out answer!);

    public static void StoreAnswer(string faqKey, string locale, string answer)
    {
        if (!string.IsNullOrWhiteSpace(answer))
            Cache[BuildKey(faqKey, locale)] = answer;
    }

    private static string BuildKey(string faqKey, string locale) =>
        $"{faqKey}|{NormalizeLocale(locale)}";

    private static string NormalizeLocale(string? locale) =>
        !string.IsNullOrWhiteSpace(locale) && locale.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt" : "en";

    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }
        return builder.ToString();
    }
}
