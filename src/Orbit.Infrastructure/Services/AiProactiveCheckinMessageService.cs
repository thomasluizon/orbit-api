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
            - Title: at most 8 words. Use their name only where it reads naturally.
            - Body: one or two sentences. Point at ONE of the open habits, never the whole list.
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
            return lines.Length >= 2
                ? Result.Success((lines[0], lines[1]))
                : Result.Success((FallbackTitle(displayName, language), lines[0]));
        }
        catch (Exception ex)
        {
            LogProactiveCheckinGenerationFailed(logger, ex);
            return GenerateFallback(displayName, offTrackHabitTitles.Count, language);
        }
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
        var body = (openHabitCount > 1, isPtBr) switch
        {
            (true, true) => "Ainda há hábitos abertos hoje. Escolha o mais fácil e conte à Astra quando fizer.",
            (true, false) => "Some habits are still open today. Pick the easiest one and tell Astra when you do it.",
            (false, true) => "Um hábito segue aberto hoje. Conte à Astra quando você fizer.",
            _ => "One habit is still open today. Tell Astra when you do it."
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
