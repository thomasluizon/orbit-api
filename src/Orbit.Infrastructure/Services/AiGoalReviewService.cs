using Microsoft.Extensions.Logging;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed class AiGoalReviewService(
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

        var languageName = language.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "Brazilian Portuguese",
            _ => "English"
        };

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

        logger.LogInformation("Generating goal review (language: {Language})...", language);

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

            logger.LogInformation("Goal review generated successfully ({Length} chars)", trimmed.Length);
            return Result.Success(trimmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI API call failed for goal review");
            return Result.Failure<string>("AI goal review temporarily unavailable");
        }
    }
}
