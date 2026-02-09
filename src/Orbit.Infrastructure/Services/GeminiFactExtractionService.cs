using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Common;
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
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var extractionPrompt = BuildExtractionPrompt(userMessage, aiResponse);

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

    private static string BuildExtractionPrompt(string userMessage, string? aiResponse)
    {
        return $$"""
            # Extract Key Facts from Conversation

            Analyze this conversation and extract ONLY factual information the user shared about themselves.

            **User message:** {{userMessage}}
            **AI response:** {{aiResponse ?? "(no response yet)"}}

            Return JSON with this EXACT structure:
            {
              "facts": [
                {
                  "factText": "clear, concise fact statement",
                  "category": "preference" | "routine" | "context"
                }
              ]
            }

            Rules:
            - Extract ONLY explicit statements by the user about themselves
            - Do NOT infer or assume facts not directly stated
            - Each fact should be a standalone sentence
            - Category: preference (likes/dislikes/preferences), routine (schedules/patterns), context (situation/background/goals)
            - If no personal facts to extract, return {"facts": []}
            - NEVER extract action requests, commands, or habit names as facts
            - Examples of what IS a fact: "User is a morning person", "User works night shifts", "User prefers running outdoors"
            - Examples of what is NOT a fact: "User wants to create a running habit", "User logged meditation"
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
