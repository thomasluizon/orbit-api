using System.Globalization;
using System.Text;

namespace Orbit.Application.Chat;

/// <summary>
/// Cheap, deterministic pre-check that decides whether a chat turn can skip the full tool-declaration
/// payload (~40 tools). Only clearly-trivial social turns — greetings, thanks, acknowledgements in
/// English or Portuguese — are routed tool-free; anything with actionable content, a question, or an
/// unrecognised shape keeps the full tool loop. It biases hard toward keeping tools so a real request
/// is never starved, and runs in-process with no model call so it adds zero latency to genuine turns.
/// </summary>
public static class ChatIntentRouter
{
    private static readonly HashSet<string> TrivialPhrases = new(StringComparer.Ordinal)
    {
        "hi", "hii", "hey", "heya", "hiya", "hello", "helo", "yo", "sup", "hi there", "hey there",
        "thanks", "thank you", "thanks a lot", "thanks so much", "thank you so much", "thanks man",
        "thx", "ty", "tysm", "thank u", "cheers", "much appreciated", "appreciate it", "thanks again",
        "ok", "okay", "k", "kk", "okey", "alright", "alrighty", "got it", "gotcha", "understood",
        "makes sense", "sounds good", "sounds great", "will do", "noted",
        "cool", "nice", "nice one", "great", "awesome", "perfect", "amazing", "wonderful", "excellent",
        "fantastic", "sweet", "brilliant", "good", "good job", "great job", "well done", "nice work",
        "lol", "lmao", "haha", "hahaha", "hehe",
        "good morning", "good afternoon", "good evening", "good night", "morning", "evening", "gn", "gm",
        "bye", "goodbye", "see you", "see ya", "cya", "later", "see you later", "take care",
        "yes", "yep", "yeah", "yup", "no", "nope", "nah", "sure", "of course", "indeed",
        "np", "no problem", "you too", "same to you",
        "oi", "ola", "e ai", "eai", "opa", "fala", "oi tudo bem",
        "obrigado", "obrigada", "obg", "valeu", "vlw", "muito obrigado", "muito obrigada", "agradecido",
        "beleza", "blz", "tranquilo", "suave", "show", "show de bola", "massa", "legal", "bacana",
        "otimo", "perfeito", "maravilha", "entendi", "entendido", "saquei", "certo", "ta", "ta bom",
        "ta certo", "fechou", "combinado", "bom dia", "boa tarde", "boa noite",
        "tchau", "ate mais", "ate logo", "falou", "flw",
        "sim", "claro", "com certeza", "isso", "exato", "nao", "nops"
    };

    /// <summary>
    /// True when the turn is a clearly-trivial social nicety that needs no tools. Conservative:
    /// any unrecognised, longer, or question-shaped message returns false (keep the full tool loop).
    /// </summary>
    public static bool IsNoToolTurn(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalized = Normalize(message);
        if (normalized.Length == 0)
            return true;

        return TrivialPhrases.Contains(normalized);
    }

    private static string Normalize(string message)
    {
        var decomposed = message.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetter(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
            else if (char.IsWhiteSpace(ch) && !lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        var result = builder.ToString();
        return result.EndsWith(' ') ? result[..^1] : result;
    }
}
