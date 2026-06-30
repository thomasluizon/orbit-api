using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiProactiveCheckinMessageService(
    AiCompletionClient aiClient,
    ILogger<AiProactiveCheckinMessageService> logger) : IProactiveCheckinMessageService
{
    public async Task<Result<(string Title, string Body)>> GenerateMessageAsync(
        string displayName,
        IReadOnlyList<string> offTrackHabitTitles,
        int currentStreak,
        string language,
        CancellationToken cancellationToken = default)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);
        var habitList = string.Join(", ", offTrackHabitTitles);
        var streakContext = currentStreak > 0
            ? $"They currently have a {currentStreak}-day streak going."
            : "They do not have an active streak right now.";

        var prompt = $"""
            User's name: {displayName}
            They have fallen behind today on these habits: {habitList}
            {streakContext}

            Generate a short, proactive check-in push notification from Astra (their habit coach) that gently nudges them to get back on track before the day ends.

            Rules:
            - Return EXACTLY two lines: first line is the notification title, second line is the body
            - Title: 5-8 words max, personal and encouraging (use their name when it feels natural)
            - Body: 1-2 sentences max, supportive and specific to what they fell behind on
            - Be creative and varied -- don't use the same structure every time
            - Tone: supportive friend, not preachy or judgmental
            - Do NOT use emojis
            - You may use the names "Astra" and "Orbit" but no other brand names
            - Write ONLY in {languageName}
            - No quotes or formatting, just plain text
            """;

        try
        {
            var text = await aiClient.CompleteTextAsync(
                "You are Astra, a supportive habit coach sending a proactive check-in push notification to help someone get back on track with the habits they fell behind on today.",
                prompt,
                temperature: 0.9,
                cancellationToken,
                purpose: "proactive_checkin");

            if (string.IsNullOrWhiteSpace(text))
            {
                LogEmptyProactiveCheckinResponse(logger);
                return GenerateFallback(displayName, language);
            }

            var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length >= 2)
                return Result.Success((lines[0], lines[1]));

            var fallbackTitle = LocaleHelper.IsPortuguese(language)
                ? $"Ainda dá tempo hoje, {displayName}"
                : $"Still time today, {displayName}";
            return Result.Success((fallbackTitle, lines[0]));
        }
        catch (Exception ex)
        {
            LogProactiveCheckinGenerationFailed(logger, ex);
            return GenerateFallback(displayName, language);
        }
    }

    private static Result<(string Title, string Body)> GenerateFallback(string displayName, string language)
    {
        return LocaleHelper.IsPortuguese(language)
            ? Result.Success(($"Ainda dá tempo hoje, {displayName}",
                "Você ficou para trás em alguns hábitos hoje. A Astra está aqui -- bora retomar?"))
            : Result.Success(($"Still time today, {displayName}",
                "You've fallen behind on a few habits today. Astra's got your back -- let's get back on track."));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "AI returned empty response for proactive check-in message")]
    private static partial void LogEmptyProactiveCheckinResponse(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to generate proactive check-in message via AI")]
    private static partial void LogProactiveCheckinGenerationFailed(ILogger logger, Exception ex);

}
