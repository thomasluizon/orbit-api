using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services.Prompts;
using System.Text.RegularExpressions;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiSlipAlertMessageService(
    BudgetedAiCompletionClient aiClient,
    ILogger<AiSlipAlertMessageService> logger) : ISlipAlertMessageService
{
    internal const string SystemPrompt =
        "You help someone follow through on the positive choice behind a habit they want to avoid. "
        + "You write one short, specific, warm push notification. Encourage them directly; never sell, hype or scold.";

    internal static string BuildPrompt(string habitTitle, DayOfWeek dayOfWeek, int? peakHour, string language)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);

        var timeContext = peakHour.HasValue
            ? $"They tend to slip around {peakHour.Value}:00 on {dayOfWeek}s, and this notification reaches them about two hours before that."
            : $"They tend to slip on {dayOfWeek}s with no time pattern, and this notification reaches them in the morning.";

        return $"""
            Bad habit: {PromptDataSanitizer.QuoteInline(habitTitle, 100)}
            Pattern: {timeContext}

            Interpret what the person wants to avoid, then name the positive goal they can follow today.
            An avoid-form title describes the unwanted behaviour, not the goal: "Cheat diet" means keeping to their diet; "Smoking" means staying smoke-free; "Comer besteira" means sticking with planned meals.
            Write a short, specific, encouraging notification about that positive goal in the person's language, even if the habit title is in another language.
            Never paste or quote the raw habit title into a fixed sentence. Do not repeat a foreign-language title. If its meaning is unclear, encourage their choice without naming the habit.

            Format:
            - Return EXACTLY two lines. The first line is the notification title, the second line is the body.
            - Title must use sentence case: capitalise only the first word, proper nouns and product names (Astra, Orbit); never use title case
            - Title: at most 8 words.
            - Body: one or two short, natural sentences, specific to the positive goal and to the pattern above.
            - Never say or imply that it is now the usual time. The notification always arrives before it, and when there is no time pattern there is no usual time to name.
            - Do not name the app.
            - Write ONLY in {languageName}.
            - Plain text only, no Markdown. Write both the title and the body yourself.

            {NotificationVoice.SlipAlertRules}
            """;
    }

    public async Task<Result<(string Title, string Body)>> GenerateMessageAsync(
        string habitTitle,
        DayOfWeek dayOfWeek,
        int? peakHour,
        string language,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(habitTitle, dayOfWeek, peakHour, language);

        try
        {
            var text = await aiClient.CompleteTextAsync(
                SystemPrompt,
                prompt,
                temperature: 0.9,
                cancellationToken: cancellationToken,
                purpose: "slip_alert");

            if (string.IsNullOrWhiteSpace(text))
            {
                LogEmptySlipAlertResponse(logger);
                return GenerateFallback(language);
            }

            var lines = AiPlainText.Normalize(text, [habitTitle])
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length != 2 || ContainsRawForeignTitle(lines, habitTitle, language))
            {
                LogUnusableSlipAlertResponse(logger);
                return GenerateFallback(language);
            }

            return Result.Success((lines[0], lines[1]));
        }
        catch (AiBudgetExceededException)
        {
            return GenerateFallback(language);
        }
        catch (Exception ex)
        {
            LogSlipAlertGenerationFailed(logger, ex);
            return GenerateFallback(language);
        }
    }

    /// <summary>
    /// Local copy cannot interpret the habit, so it names neither the habit nor a usual time.
    /// </summary>
    private static Result<(string Title, string Body)> GenerateFallback(string language)
    {
        var body = LocaleHelper.IsPortuguese(language)
            ? "Hoje, você pode seguir o que escolheu para você."
            : "You can stick with what you chose for yourself today.";

        return Result.Success((FallbackTitle(language), body));
    }

    /// <inheritdoc cref="GenerateFallback"/>
    private static string FallbackTitle(string language) => LocaleHelper.IsPortuguese(language)
        ? "Uma escolha de cada vez"
        : "One choice at a time";

    private static bool ContainsRawForeignTitle(string[] lines, string habitTitle, string language)
    {
        var title = PromptDataSanitizer.SanitizeInline(habitTitle, 100);
        if (string.IsNullOrWhiteSpace(title)
            || !lines.Any(line => line.Contains(title, StringComparison.OrdinalIgnoreCase)))
            return false;

        var words = HabitWords().Matches(title).Select(match => match.Value).ToArray();
        var hasEnglish = words.Any(EnglishHabitWords.Contains);
        var hasPortuguese = words.Any(PortugueseHabitWords.Contains);
        if (hasEnglish == hasPortuguese)
            return false;

        return LocaleHelper.IsPortuguese(language) ? hasEnglish : hasPortuguese;
    }

    /// <summary>
    /// Distinct habit vocabulary supplies evidence in either language. Shared words, names,
    /// borrowed phrases and mixed-language titles stay with the model when evidence is uncertain.
    /// </summary>
    private static readonly HashSet<string> EnglishHabitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cheat", "diet", "smoking", "smoke", "scrolling", "gambling", "drinking", "overeating", "biting", "nails"
    };

    private static readonly HashSet<string> PortugueseHabitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fumar", "cigarro", "cigarros", "comer", "besteira", "besteiras", "dieta", "doces", "roer", "unhas", "beber", "álcool"
    };

    [GeneratedRegex(@"\p{L}+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HabitWords();

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "AI returned empty response for slip alert message")]
    private static partial void LogEmptySlipAlertResponse(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to generate slip alert message via AI")]
    private static partial void LogSlipAlertGenerationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "AI returned unusable response for slip alert message")]
    private static partial void LogUnusableSlipAlertResponse(ILogger logger);

}
