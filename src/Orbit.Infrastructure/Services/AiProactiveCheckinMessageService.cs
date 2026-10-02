using System.Globalization;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services.Prompts;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiProactiveCheckinMessageService(
    AiCompletionClient aiClient,
    ILogger<AiProactiveCheckinMessageService> logger) : IProactiveCheckinMessageService
{
    private const int MaxBodyTextElements = 60;

    internal const string SystemPrompt =
        "You are Astra. You write one check-in push notification for someone whose day still has open habits. "
        + "You make the next step small and obvious. You never sell, never perform enthusiasm, and never scold.";

    internal static string BuildPrompt(
        string displayName, IReadOnlyList<string> offTrackHabitTitles, int currentStreak, string language)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);
        var sanitizedDisplayName = PromptDataSanitizer.SanitizeInline(displayName, AppConstants.MaxUserNameLength);
        var habitList = string.Join(", ", offTrackHabitTitles.Select(title => PromptDataSanitizer.QuoteInline(title, 100)));
        var streakContext = currentStreak > 0
            ? $"They currently have a {currentStreak}-day streak going."
            : "They do not have an active streak right now.";

        return $"""
            User's name: {sanitizedDisplayName}
            They have fallen behind today on these habits: {habitList}
            {streakContext}

            Write a check-in push notification that makes it easy to pick one of these back up before the day ends.

            Format:
            - Return EXACTLY two lines. The first line is the notification title, the second line is the body.
            - Title must use sentence case: capitalise only the first word, proper nouns and product names (Astra, Orbit); never use title case
            - Title: at most 8 words. Use their name only where it reads naturally.
            - Body: one sentence, at most {MaxBodyTextElements} characters. Point at one of the open habits, never the whole list.
            - Count text elements after trimming; an emoji counts as one character
            - Body must be a complete sentence that stands on its own without the title
            - Start the body with an uppercase letter in the requested language
            - You may write the names Astra and Orbit. Use no other brand name.
            - Write ONLY in {languageName}.

            {NotificationVoice.Rules}
            """;
    }

    public async Task<Result<(string Title, string Body)>> GenerateMessageAsync(
        string displayName,
        IReadOnlyList<string> offTrackHabitTitles,
        int currentStreak,
        string language,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(displayName, offTrackHabitTitles, currentStreak, language);

        try
        {
            var text = await aiClient.CompleteTextAsync(
                SystemPrompt,
                prompt,
                temperature: 0.9,
                cancellationToken: cancellationToken,
                purpose: "proactive_checkin");

            if (string.IsNullOrWhiteSpace(text))
            {
                LogEmptyProactiveCheckinResponse(logger);
                return GenerateFallback(displayName, offTrackHabitTitles.Count, language);
            }

            var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var title = lines.Length >= 2 ? lines[0] : FallbackTitle(displayName, language);
            var body = NormalizeBody(lines.Length >= 2 ? lines[1] : lines[0], language);
            if (new StringInfo(body).LengthInTextElements > MaxBodyTextElements)
                body = GenerateFallback(displayName, offTrackHabitTitles.Count, language).Value.Body;

            return Result.Success((title, body));
        }
        catch (Exception ex)
        {
            LogProactiveCheckinGenerationFailed(logger, ex);
            return GenerateFallback(displayName, offTrackHabitTitles.Count, language);
        }
    }

    private static string NormalizeBody(string body, string language)
    {
        var culture = CultureInfo.GetCultureInfo(LocaleHelper.IsPortuguese(language) ? "pt-BR" : "en");
        for (var index = 0; index < body.Length; index++)
        {
            if (!char.IsLetter(body[index]))
                continue;

            return body[..index] + char.ToUpper(body[index], culture) + body[(index + 1)..];
        }

        return body;
    }

    /// <summary>
    /// The copy that ships whenever the model is down, so it claims only what the scheduler and the
    /// tools can stand behind. <c>ProactiveCheckinSchedulerService</c> sends as soon as one habit is
    /// off track, which makes a single habit the ordinary case rather than the exception, and
    /// <c>LogHabitTool</c> records only the habit a person names, so nothing logs "the rest".
    /// </summary>
    private static Result<(string Title, string Body)> GenerateFallback(
        string displayName, int openHabitCount, string language)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var body = (openHabitCount == 1, isPtBr) switch
        {
            (true, true) => "Você tem 1 hábito aberto hoje.",
            (true, false) => "You have 1 habit still open today.",
            (false, true) => $"Você tem {openHabitCount} hábitos abertos hoje.",
            _ => $"You have {openHabitCount} habits still open today."
        };

        return Result.Success((FallbackTitle(displayName, language), body));
    }

    private static string FallbackTitle(string displayName, string language)
    {
        var sanitizedDisplayName = PromptDataSanitizer.SanitizeInline(displayName, AppConstants.MaxUserNameLength);
        return LocaleHelper.IsPortuguese(language)
            ? $"Ainda dá tempo hoje, {sanitizedDisplayName}"
            : $"Still time today, {sanitizedDisplayName}";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "AI returned empty response for proactive check-in message")]
    private static partial void LogEmptyProactiveCheckinResponse(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to generate proactive check-in message via AI")]
    private static partial void LogProactiveCheckinGenerationFailed(ILogger logger, Exception ex);

}
