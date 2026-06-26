using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiTagSuggestionService(
    AiCompletionClient aiClient,
    ILogger<AiTagSuggestionService> logger) : ITagSuggestionService
{
    private sealed record TagSuggestionResult(List<string> Tags);

    public async Task<Result<IReadOnlyList<string>>> SuggestTagsAsync(
        string title,
        string? description,
        IReadOnlyList<string> existingTagNames,
        string language,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(title, description, existingTagNames, language);

        if (logger.IsEnabled(LogLevel.Information))
            LogGeneratingTagSuggestions(logger, language);

        try
        {
            var completion = await aiClient.CompleteJsonAsync<TagSuggestionResult>(
                "You suggest tags for a habit and reply with a single JSON object, nothing else.",
                prompt,
                cancellationToken: cancellationToken,
                purpose: "tag_suggestion",
                tier: AiModelTier.SubTask);

            var cleaned = completion?.Tags?
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .ToList();

            if (cleaned is null || cleaned.Count == 0)
                return Result.Failure<IReadOnlyList<string>>(ErrorMessages.AiEmptyResponse);

            if (logger.IsEnabled(LogLevel.Information))
                LogTagSuggestionsGenerated(logger, cleaned.Count);

            return Result.Success<IReadOnlyList<string>>(cleaned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTagSuggestionFailed(logger, ex);
            return Result.Failure<IReadOnlyList<string>>(ErrorMessages.AiTagSuggestionUnavailable);
        }
    }

    internal static string BuildPrompt(
        string title,
        string? description,
        IReadOnlyList<string> existingTagNames,
        string language)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);
        var existingTagsBlock = existingTagNames.Count > 0
            ? string.Join(", ", existingTagNames)
            : "(none yet)";
        var descriptionLine = string.IsNullOrWhiteSpace(description)
            ? "(no description)"
            : description.Trim();

        return $$"""
            HABIT
            Title: {{title}}
            Description: {{descriptionLine}}

            EXISTING TAGS (reuse one of these verbatim whenever it fits instead of inventing a near-duplicate):
            {{existingTagsBlock}}

            RULES
            - Suggest 1 to 5 short tags that categorize this habit.
            - Each tag is 1-2 words written in {{languageName}}.
            - Strongly prefer an existing tag over a new near-duplicate.
            - No '#' prefix, no punctuation, no duplicates.
            - Respond only with JSON in this exact shape: { "tags": ["tag1", "tag2"] }
            """;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Generating tag suggestions (language: {Language})...")]
    private static partial void LogGeneratingTagSuggestions(ILogger logger, string language);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Tag suggestions generated ({Count} tags)")]
    private static partial void LogTagSuggestionsGenerated(ILogger logger, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "AI API call failed for tag suggestion")]
    private static partial void LogTagSuggestionFailed(ILogger logger, Exception ex);
}
