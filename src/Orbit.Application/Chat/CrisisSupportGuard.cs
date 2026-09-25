using System.Globalization;
using System.Text;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

[Flags]
public enum CrisisLocales
{
    None = 0,
    English = 1,
    Portuguese = 2
}

public static class CrisisSupportGuard
{
    public const string EnglishResource = "Call or text 988 (Suicide and Crisis Lifeline)";
    public const string PortugueseResource = "CVV, Ligue 188 (Centro de Valorizacao da Vida)";
    public const string EnglishSupport = "I'm sorry you're going through this. You deserve support right now.";
    public const string PortugueseSupport = "Sinto muito que você esteja passando por isso. Você merece apoio agora.";

    private static readonly string[] EnglishPhrases =
    [
        "hurt myself", "harm myself", "self harm", "self-harm", "kill myself",
        "end my life", "take my life", "want to die", "don't want to live",
        "suicide", "suicidal"
    ];

    private static readonly string[] PortuguesePhrases =
    [
        "me machucar", "me ferir", "me matar", "tirar minha vida",
        "acabar com minha vida", "quero morrer", "nao quero viver",
        "suicidio", "suicida", "automutilacao"
    ];

    public static CrisisLocales Detect(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return CrisisLocales.None;

        var normalized = Normalize(message);
        var locales = CrisisLocales.None;
        if (EnglishPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal)))
            locales |= CrisisLocales.English;
        if (PortuguesePhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal)))
            locales |= CrisisLocales.Portuguese;
        return locales;
    }

    public static string LocaleLabel(CrisisLocales locales) => locales switch
    {
        CrisisLocales.Portuguese => "pt",
        CrisisLocales.English | CrisisLocales.Portuguese => "en,pt",
        _ => "en"
    };

    public static string FallbackMessage(CrisisLocales locales) =>
        locales == CrisisLocales.Portuguese ? PortugueseSupport : EnglishSupport;

    public static string EnsureResources(string? reply, CrisisLocales locales, IReadOnlyList<ChatHistoryMessage>? history)
    {
        var text = reply ?? string.Empty;
        var lastAssistant = history?.LastOrDefault();
        var priorReply = lastAssistant is not null
            && ChatHistoryMessage.NormalizeRole(lastAssistant.Role) == ChatHistoryMessage.AssistantRole
                ? lastAssistant.Content
                : string.Empty;

        var lines = new List<string>(2);
        if (locales.HasFlag(CrisisLocales.English)
            && !ContainsResource(text, EnglishResource)
            && !ContainsResource(priorReply, EnglishResource))
            lines.Add(EnglishResource);
        if (locales.HasFlag(CrisisLocales.Portuguese)
            && !ContainsResource(text, PortugueseResource)
            && !ContainsResource(priorReply, PortugueseResource))
            lines.Add(PortugueseResource);

        if (lines.Count == 0)
            return text;

        return string.IsNullOrWhiteSpace(text)
            ? string.Join("\n", lines)
            : $"{text.TrimEnd()}\n\n{string.Join("\n", lines)}";
    }

    private static bool ContainsResource(string text, string resource) =>
        text.Contains(resource, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(ch is '\u2019' or '\u2018' or '\u02bc' or '\uff07' ? '\'' : ch);
        }
        return builder.ToString();
    }
}
