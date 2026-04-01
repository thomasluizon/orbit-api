using Microsoft.Extensions.Logging;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed class AiSlipAlertMessageService(
    AiCompletionClient aiClient,
    ILogger<AiSlipAlertMessageService> logger) : ISlipAlertMessageService
{
    public async Task<Result<(string Title, string Body)>> GenerateMessageAsync(
        string habitTitle,
        DayOfWeek dayOfWeek,
        int? peakHour,
        string language,
        CancellationToken cancellationToken = default)
    {
        var languageName = language.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "Brazilian Portuguese",
            _ => "English"
        };

        var timeContext = peakHour.HasValue
            ? $"They tend to slip around {peakHour.Value}:00 on {dayOfWeek}s."
            : $"They tend to slip on {dayOfWeek}s (no specific time pattern).";

        var prompt = $"""
            Bad habit: "{habitTitle}"
            Pattern: {timeContext}

            Generate a short, inspiring push notification to help them stay strong today.

            Rules:
            - Return EXACTLY two lines: first line is the notification title, second line is the body
            - Title: 5-8 words max, personal and warm (e.g., "Stay strong today!" or "You've got this!")
            - Body: 1-2 sentences max, motivational and specific to their habit
            - Be creative and varied -- don't use the same structure every time
            - Tone: supportive friend, not preachy or judgmental
            - Do NOT use emojis
            - Do NOT mention the app name
            - Write ONLY in {languageName}
            - No quotes or formatting, just plain text
            """;

        try
        {
            var text = await aiClient.CompleteTextAsync(
                "You are a supportive habit coach sending a push notification to help someone avoid a bad habit slip-up.",
                prompt,
                temperature: 0.9,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("AI returned empty response for slip alert message");
                return GenerateFallback(habitTitle, language);
            }

            var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length >= 2)
                return Result.Success((lines[0], lines[1]));

            var fallbackTitle = language.StartsWith("pt") ? $"Fique atento: {habitTitle}" : $"Heads up: {habitTitle}";
            return Result.Success((fallbackTitle, lines[0]));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate slip alert message via AI");
            return GenerateFallback(habitTitle, language);
        }
    }

    private static Result<(string Title, string Body)> GenerateFallback(string habitTitle, string language)
    {
        return language.StartsWith("pt")
            ? Result.Success(($"Fique atento: {habitTitle}",
                "Você costuma deslizar por volta desse horário. Força -- você consegue!"))
            : Result.Success(($"Heads up: {habitTitle}",
                "You tend to slip around this time. Stay strong -- you've got this!"));
    }
}
