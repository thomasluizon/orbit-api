using Microsoft.Extensions.Logging;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiFactExtractionService(
    AiCompletionClient aiClient,
    ILogger<AiFactExtractionService> logger) : IFactExtractionService
{
    public async Task<Result<ExtractedFacts>> ExtractFactsAsync(
        string userMessage,
        string? aiResponse,
        IReadOnlyList<UserFact> existingFacts,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var prompt = BuildExtractionPrompt(userMessage, aiResponse, existingFacts);

        try
        {
            LogCallingFactExtraction(logger);

            var facts = await aiClient.CompleteJsonAsync<ExtractedFacts>(prompt, temperature: 0.1, cancellationToken);

            stopwatch.Stop();
            LogFactExtractionResponded(logger, stopwatch.ElapsedMilliseconds);

            if (facts is null)
            {
                LogEmptyFactResponse(logger);
                return Result.Success(new ExtractedFacts { Facts = [] });
            }

            LogFactsExtracted(logger, facts.Facts.Count);
            return Result.Success(facts);
        }
        catch (System.Text.Json.JsonException ex)
        {
            LogFactDeserializationFailed(logger, ex);
            return Result.Success(new ExtractedFacts { Facts = [] });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFactExtractionFailed(logger, ex);
            return Result.Success(new ExtractedFacts { Facts = [] });
        }
    }

    private static string BuildExtractionPrompt(string userMessage, string? aiResponse, IReadOnlyList<UserFact> existingFacts)
    {
        var existingFactsList = existingFacts.Count > 0
            ? string.Join("\n", existingFacts.Select(f => $"- [{f.Category}] {f.FactText}"))
            : "(none)";

        return $$"""
            # Extract Personal Facts from Conversation

            Your job is to extract facts that reveal WHO the user IS — their life situation, schedule constraints, health context, personality, and genuine preferences.
            These facts help personalize habit suggestions in future conversations.

            **User message:** {{userMessage}}
            **AI response:** {{aiResponse ?? "(no response yet)"}}

            **Already stored facts (do NOT duplicate these):**
            {{existingFactsList}}

            Return JSON with this EXACT structure:
            {
              "facts": [
                {
                  "factText": "User [fact about who they are]",
                  "category": "preference" | "routine" | "context"
                }
              ]
            }

            ## What TO extract (facts about WHO the person is):
            - Life context: "User works from home", "User is a student", "User has a young child", "User travels frequently for work"
            - Schedule constraints: "User works night shifts", "User has morning meetings", "User studies in the afternoons"
            - Health context: "User takes medication every morning", "User has a bad knee", "User is training for a marathon"
            - Genuine preferences: "User prefers outdoor activities over gym", "User dislikes early mornings", "User is a vegetarian"
            - Personality traits: "User is a night owl", "User gets stressed easily at work", "User finds meditation improves their focus"

            ## What NOT to extract:

            **NEVER extract habit creation or tracking requests** — when a user says "I want to meditate daily", "Create a morning routine", "I want to stop smoking", "track my gym sessions", these are habit intentions, NOT personal facts. The habit list already captures this. Do NOT save:
            - "User wants to meditate in the morning" ← this is a habit, not a personal fact
            - "User wants to go to the gym on Monday and Friday" ← this is a habit schedule
            - "User wants to stop biting their nails" ← this is a habit to track
            - "User wants to drink water every day" ← this is a habit
            - "User wants to do yoga every 2 weeks" ← this is a habit

            **NEVER extract one-time events or logged completions:**
            - "User meditated this morning" ← single event, not a lasting trait
            - "User logged their workout" ← action taken, not a personal fact
            - "User completed their habit" ← completion event

            **NEVER extract transient emotional states:**
            - "User felt super focused after meditating" ← one-time feeling
            - "User is tired today" ← temporary state
            - "User had a good day" ← not lasting

            ## Additional rules:
            - If the message is only about creating/logging habits with no personal context revealed, return {"facts": []}
            - DO NOT duplicate facts already in the stored list
            - If the user contradicts an existing fact, extract the NEW fact — the system will handle replacement
            - Facts must be genuinely useful for personalizing habit suggestions in future conversations
            - Category: preference (likes/dislikes/personal style), routine (real schedule patterns and constraints), context (life situation, goals, background)
            """;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Calling AI API for fact extraction...")]
    private static partial void LogCallingFactExtraction(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "AI fact extraction responded in {ElapsedMs}ms")]
    private static partial void LogFactExtractionResponded(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "AI returned empty response - no facts to extract")]
    private static partial void LogEmptyFactResponse(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Extracted {FactCount} facts from conversation")]
    private static partial void LogFactsExtracted(ILogger logger, int factCount);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Failed to deserialize fact extraction response - returning empty facts")]
    private static partial void LogFactDeserializationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Fact extraction failed - non-critical error")]
    private static partial void LogFactExtractionFailed(ILogger logger, Exception ex);

}
