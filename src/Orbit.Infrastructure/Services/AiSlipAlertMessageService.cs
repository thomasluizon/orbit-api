using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services.Prompts;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiSlipAlertMessageService(
    AiCompletionClient aiClient,
    ILogger<AiSlipAlertMessageService> logger) : ISlipAlertMessageService
{
    internal const string SystemPrompt =
        "You are a calm, steady presence helping someone stay away from a habit they are trying to quit. "
        + "You write one push notification. You never sell, never perform enthusiasm, and never scold.";

    internal static string BuildPrompt(string habitTitle, DayOfWeek dayOfWeek, int? peakHour, string language)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);

        var timeContext = peakHour.HasValue
            ? $"They tend to slip around {peakHour.Value}:00 on {dayOfWeek}s."
            : $"They tend to slip on {dayOfWeek}s (no specific time pattern).";

        return $"""
            Bad habit: {PromptDataSanitizer.QuoteInline(habitTitle, 100)}
            Pattern: {timeContext}

            Write a push notification that helps this person let it pass today.

            Format:
            - Return EXACTLY two lines. The first line is the notification title, the second line is the body.
            - Title: at most 8 words.
            - Body: one or two sentences, specific to this habit and to the pattern above.
            - Do not name the app.
            - Write ONLY in {languageName}.

            {NotificationVoice.Rules}
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
                return GenerateFallback(habitTitle, language);
            }

            var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length >= 2
                ? Result.Success((lines[0], lines[1]))
                : Result.Success((FallbackTitle(habitTitle, language), lines[0]));
        }
        catch (Exception ex)
        {
            LogSlipAlertGenerationFailed(logger, ex);
            return GenerateFallback(habitTitle, language);
        }
    }

    private static Result<(string Title, string Body)> GenerateFallback(string habitTitle, string language) =>
        Result.Success((
            FallbackTitle(habitTitle, language),
            LocaleHelper.IsPortuguese(language)
                ? "É por volta desta hora que costuma aparecer. Hoje você pode deixar passar."
                : "This is around the time it usually comes up. You can let it pass today."));

    private static string FallbackTitle(string habitTitle, string language)
    {
        var sanitizedHabitTitle = SanitizeHeadingTitle(habitTitle);
        return LocaleHelper.IsPortuguese(language)
            ? $"Seu horário de sempre: {sanitizedHabitTitle}"
            : $"Your usual time for {sanitizedHabitTitle}";
    }

    private static string SanitizeHeadingTitle(string habitTitle) =>
        habitTitle.Length == 0 ? string.Empty : PromptDataSanitizer.SanitizeInline(habitTitle, 100);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "AI returned empty response for slip alert message")]
    private static partial void LogEmptySlipAlertResponse(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to generate slip alert message via AI")]
    private static partial void LogSlipAlertGenerationFailed(ILogger logger, Exception ex);

}
