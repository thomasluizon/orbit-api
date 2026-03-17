using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class GeminiFactExtractionService(
    HttpClient httpClient,
    IOptions<GeminiSettings> options,
    ILogger<GeminiFactExtractionService> logger) : IFactExtractionService
{
    private readonly GeminiSettings _settings = options.Value;

    private static readonly JsonSerializerOptions FactsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<Result<ExtractedFacts>> ExtractFactsAsync(
        string userMessage,
        string? aiResponse,
        IReadOnlyList<UserFact> existingFacts,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var extractionPrompt = BuildExtractionPrompt(userMessage, aiResponse, existingFacts);

        var request = new GeminiRequest
        {
            Contents = new[]
            {
                new GeminiContent
                {
                    Parts = new[]
                    {
                        new GeminiPart { Text = extractionPrompt }
                    }
                }
            },
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.1,
                ResponseMimeType = "application/json"
            }
        };

        try
        {
            logger.LogInformation("🔵 Calling Gemini API for fact extraction...");

            var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

            // Retry logic for rate limiting
            HttpResponseMessage? response = null;
            int retryCount = 0;
            int maxRetries = 3;

            while (retryCount <= maxRetries)
            {
                response = await httpClient.PostAsJsonAsync(url, request, cancellationToken);

                if (response.IsSuccessStatusCode || response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    break;
                }

                retryCount++;
                if (retryCount <= maxRetries)
                {
                    var delayMs = (int)Math.Pow(2, retryCount) * 1000; // Exponential backoff: 2s, 4s, 8s
                    logger.LogWarning("⚠️  Rate limited. Retrying in {DelayMs}ms (attempt {Retry}/{Max})...",
                        delayMs, retryCount, maxRetries);
                    await Task.Delay(delayMs, cancellationToken);
                }
            }

            stopwatch.Stop();
            logger.LogInformation("✅ Gemini fact extraction responded in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

            if (!response!.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Gemini API returned {Status}: {Body}", response.StatusCode, errorBody);
                return Result.Failure<ExtractedFacts>($"Gemini API error: {response.StatusCode}");
            }

            var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);
            var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogInformation("Gemini returned empty response - no facts to extract");
                return Result.Success(new ExtractedFacts { Facts = [] });
            }

            logger.LogInformation("📄 GEMINI FACT EXTRACTION JSON: {Json}", text);

            var facts = JsonSerializer.Deserialize<ExtractedFacts>(text, FactsJsonOptions);

            if (facts is null)
            {
                logger.LogWarning("Failed to deserialize fact extraction response - returning empty facts");
                return Result.Success(new ExtractedFacts { Facts = [] });
            }

            logger.LogInformation("✅ Extracted {FactCount} facts from conversation", facts.Facts.Count);

            return Result.Success(facts);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize fact extraction response - returning empty facts");
            return Result.Success(new ExtractedFacts { Facts = [] });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fact extraction failed - non-critical error");
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

    // --- Gemini API DTOs ---

    private record GeminiRequest
    {
        [JsonPropertyName("contents")]
        public GeminiContent[] Contents { get; init; } = [];

        [JsonPropertyName("generationConfig")]
        public GeminiGenerationConfig? GenerationConfig { get; init; }
    }

    private record GeminiContent
    {
        [JsonPropertyName("parts")]
        public GeminiPart[] Parts { get; init; } = [];
    }

    private record GeminiPart
    {
        [JsonPropertyName("text")]
        public string Text { get; init; } = string.Empty;
    }

    private record GeminiGenerationConfig
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("responseMimeType")]
        public string ResponseMimeType { get; init; } = string.Empty;
    }

    private record GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public GeminiCandidate[]? Candidates { get; init; }
    }

    private record GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiContent? Content { get; init; }
    }
}
