using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiGoalReviewService(
    AiCompletionClient aiClient,
    ILogger<AiGoalReviewService> logger) : IGoalReviewService
{
    public async Task<Result<string>> GenerateReviewAsync(
        string goalsContext,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(goalsContext))
            return Result.Failure<string>("No goals data provided.");

        var languageName = LocaleHelper.GetAiLanguageName(language);

        var prompt = $"""
            GOALS DATA:
            {goalsContext}

            RULES:
            - Write a natural-language review in {languageName}
            - 4-6 sentences maximum
            - Mention what is on track, what is at risk, and actionable suggestions
            - Be encouraging but honest
            - No markdown formatting, no emojis, no JSON
            - Plain text only
            """;

        if (logger.IsEnabled(LogLevel.Information))
            LogGeneratingGoalReview(logger, language);

        try
        {
            var text = await aiClient.CompleteTextAsync(
                "You are a goal progress coach. Review the user's active goals and provide a concise summary.",
                prompt,
                temperature: 0.7,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<string>("AI returned empty response");

            var trimmed = AiSummaryService.StripMarkdownFences(text);

            if (logger.IsEnabled(LogLevel.Information))
                LogGoalReviewGenerated(logger, trimmed.Length);
            return Result.Success(trimmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGoalReviewFailed(logger, ex);
            return Result.Failure<string>("AI goal review temporarily unavailable");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Generating goal review (language: {Language})...")]
    private static partial void LogGeneratingGoalReview(ILogger logger, string language);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Goal review generated successfully ({Length} chars)")]
    private static partial void LogGoalReviewGenerated(ILogger logger, int length);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "AI API call failed for goal review")]
    private static partial void LogGoalReviewFailed(ILogger logger, Exception ex);

}
