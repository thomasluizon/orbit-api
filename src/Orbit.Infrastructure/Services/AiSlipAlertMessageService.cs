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
            ? $"They tend to slip around {peakHour.Value}:00 on {dayOfWeek}s, and this notification reaches them about two hours before that."
            : $"They tend to slip on {dayOfWeek}s with no time pattern, and this notification reaches them in the morning.";

        return $"""
            Bad habit: {PromptDataSanitizer.QuoteInline(habitTitle, 100)}
            Pattern: {timeContext}

            Write a push notification that helps this person let it pass today.

            Format:
            - Return EXACTLY two lines. The first line is the notification title, the second line is the body.
            - Title: at most 8 words.
            - Body: one or two sentences, specific to this habit and to the pattern above.
            - Never say or imply that it is now the usual time. The notification always arrives before it, and when there is no time pattern there is no usual time to name.
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
                return GenerateFallback(habitTitle, peakHour, language);
            }

            var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length >= 2
                ? Result.Success((lines[0], lines[1]))
                : Result.Success((FallbackTitle(habitTitle, peakHour, language), lines[0]));
        }
        catch (Exception ex)
        {
            LogSlipAlertGenerationFailed(logger, ex);
            return GenerateFallback(habitTitle, peakHour, language);
        }
    }

    /// <summary>
    /// The copy that ships whenever the model is down, so it states only what the scheduler can
    /// stand behind. <c>SlipAlertSchedulerService.CalculateAlertTime</c> sends two hours before the
    /// peak hour, and at 08:00 when <c>SlipPattern.PeakHour</c> is null, so no send is ever at the
    /// usual time and a day-only pattern has no usual time at all.
    /// </summary>
    private static Result<(string Title, string Body)> GenerateFallback(string habitTitle, int? peakHour, string language)
    {
        var isPtBr = LocaleHelper.IsPortuguese(language);
        var body = peakHour.HasValue
            ? isPtBr
                ? "Isso costuma aparecer mais tarde hoje. Você pode deixar passar."
                : "This tends to come up later today. You can let it pass."
            : isPtBr
                ? "Hoje é um dos dias em que isso costuma aparecer. Você pode deixar passar."
                : "Today is one of the days this tends to come up. You can let it pass.";

        return Result.Success((FallbackTitle(habitTitle, peakHour, language), body));
    }

    /// <inheritdoc cref="GenerateFallback"/>
    private static string FallbackTitle(string habitTitle, int? peakHour, string language)
    {
        var sanitizedHabitTitle = SanitizeHeadingTitle(habitTitle);
        var isPtBr = LocaleHelper.IsPortuguese(language);

        return peakHour.HasValue
            ? isPtBr
                ? $"Antes do horário de costume: {sanitizedHabitTitle}"
                : $"Ahead of the usual time for {sanitizedHabitTitle}"
            : isPtBr
                ? $"Um lembrete tranquilo: {sanitizedHabitTitle}"
                : $"A quiet note about {sanitizedHabitTitle}";
    }

    private static string SanitizeHeadingTitle(string habitTitle) =>
        habitTitle.Length == 0 ? string.Empty : PromptDataSanitizer.SanitizeInline(habitTitle, 100);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "AI returned empty response for slip alert message")]
    private static partial void LogEmptySlipAlertResponse(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to generate slip alert message via AI")]
    private static partial void LogSlipAlertGenerationFailed(ILogger logger, Exception ex);

}
