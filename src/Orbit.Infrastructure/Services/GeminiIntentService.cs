using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class GeminiIntentService(
    HttpClient httpClient,
    IOptions<GeminiSettings> options,
    ILogger<GeminiIntentService> logger) : IAiIntentService
{
    private readonly GeminiSettings _settings = options.Value;

    private static readonly JsonSerializerOptions ActionPlanJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<Result<AiActionPlan>> InterpretAsync(
        string userMessage,
        IReadOnlyList<Habit> activeHabits,
        IReadOnlyList<UserFact> userFacts,
        byte[]? imageData = null,
        string? imageMimeType = null,
        IReadOnlyList<RoutinePattern>? routinePatterns = null,
        IReadOnlyList<Tag>? userTags = null,
        DateOnly? userToday = null,
        IReadOnlyDictionary<Guid, HabitMetrics>? habitMetrics = null,
        IReadOnlyList<ChatHistoryMessage>? history = null,
        CancellationToken cancellationToken = default)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        logger.LogInformation("🔵 START: Building system prompt...");
        var promptStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var systemPrompt = SystemPromptBuilder.BuildSystemPrompt(
            activeHabits, userFacts,
            hasImage: imageData != null,
            routinePatterns: routinePatterns,
            userTags: userTags,
            userToday: userToday,
            habitMetrics: habitMetrics);
        promptStopwatch.Stop();
        logger.LogInformation("✅ System prompt built in {ElapsedMs}ms (length: {Length} chars)",
            promptStopwatch.ElapsedMilliseconds, systemPrompt.Length);

        // Build multi-turn conversation
        var contents = new List<GeminiContent>();

        // System prompt as first turn
        contents.Add(new GeminiContent { Role = "user", Parts = [new GeminiPart { Text = systemPrompt }] });
        contents.Add(new GeminiContent { Role = "model", Parts = [new GeminiPart { Text = "{\"actions\":[],\"aiMessage\":\"Ready.\"}" }] });

        // Conversation history
        if (history is { Count: > 0 })
        {
            foreach (var msg in history)
            {
                contents.Add(new GeminiContent
                {
                    Role = msg.Role == "user" ? "user" : "model",
                    Parts = [new GeminiPart { Text = msg.Content }]
                });
            }
        }

        // Current user message with optional image
        var currentParts = new List<GeminiPart> { new GeminiPart { Text = userMessage } };

        if (imageData != null && !string.IsNullOrWhiteSpace(imageMimeType))
        {
            currentParts.Add(new GeminiPart
            {
                InlineData = new InlineData
                {
                    MimeType = imageMimeType,
                    Data = Convert.ToBase64String(imageData)
                }
            });
        }

        contents.Add(new GeminiContent { Role = "user", Parts = currentParts.ToArray() });

        var request = new GeminiRequest
        {
            Contents = contents.ToArray(),
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.1,
                ResponseMimeType = "application/json"
            }
        };

        try
        {
            logger.LogInformation("🔵 Calling Gemini API (Model: {Model})...", _settings.Model);
            var ollamaStopwatch = System.Diagnostics.Stopwatch.StartNew();

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

            ollamaStopwatch.Stop();
            logger.LogInformation("✅ Gemini API responded in {ElapsedMs}ms", ollamaStopwatch.ElapsedMilliseconds);

            if (!response!.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Gemini API returned {Status}: {Body}", response.StatusCode, errorBody);
                return Result.Failure<AiActionPlan>($"Gemini API error: {response.StatusCode}");
            }

            logger.LogInformation("🔵 Deserializing Gemini response...");
            var deserializeStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);
            var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiActionPlan>("Gemini returned an empty response.");

            logger.LogInformation("Gemini response (length: {Length} chars)", text.Length);
            logger.LogInformation("📄 GEMINI RAW JSON: {Json}", text);

            // Fix invalid JSON escape sequences (e.g., \P, \C) that Gemini may generate
            text = Regex.Replace(text, @"\\([^""\\\/bfnrtu])", "$1");

            var plan = JsonSerializer.Deserialize<AiActionPlan>(text, ActionPlanJsonOptions);

            if (plan is null)
            {
                logger.LogError("❌ Deserialization returned null for text: {Text}", text);
                return Result.Failure<AiActionPlan>("Failed to deserialize AI response.");
            }

            logger.LogInformation("✅ Deserialized {ActionCount} actions: {ActionTypes}",
                plan.Actions.Count,
                string.Join(", ", plan.Actions.Select(a => a.Type.ToString())));

            deserializeStopwatch.Stop();
            logger.LogInformation("✅ Deserialization completed in {ElapsedMs}ms", deserializeStopwatch.ElapsedMilliseconds);

            totalStopwatch.Stop();
            logger.LogInformation("🎯 TOTAL InterpretAsync time: {ElapsedMs}ms", totalStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   ├─ Prompt build: {PromptMs}ms", promptStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   ├─ Gemini call: {GeminiMs}ms", ollamaStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   └─ Deserialize: {DeserializeMs}ms", deserializeStopwatch.ElapsedMilliseconds);

            return Result.Success(plan);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize Gemini response");
            return Result.Failure<AiActionPlan>($"Failed to parse AI response: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Gemini API call failed");
            return Result.Failure<AiActionPlan>($"AI service error: {ex.Message}");
        }
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
        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("parts")]
        public GeminiPart[] Parts { get; init; } = [];
    }

    private record GeminiPart
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("inline_data")]
        public InlineData? InlineData { get; init; }
    }

    private record InlineData
    {
        [JsonPropertyName("mime_type")]
        public string MimeType { get; init; } = string.Empty;

        [JsonPropertyName("data")]
        public string Data { get; init; } = string.Empty;
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
