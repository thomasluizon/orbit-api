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

public sealed class OllamaIntentService(
    HttpClient httpClient,
    IOptions<OllamaSettings> options,
    ILogger<OllamaIntentService> logger) : IAiIntentService
{
    private readonly OllamaSettings _settings = options.Value;

    private static readonly JsonSerializerOptions ActionPlanJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<Result<AiActionPlan>> InterpretAsync(
        string userMessage,
        IReadOnlyList<Habit> activeHabits,
        IReadOnlyList<Tag> userTags,
        IReadOnlyList<UserFact> userFacts,
        byte[]? imageData = null,
        string? imageMimeType = null,
        IReadOnlyList<RoutinePattern>? routinePatterns = null,
        CancellationToken cancellationToken = default)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Ollama doesn't support image processing
        if (imageData != null)
        {
            logger.LogWarning("Image data provided but Ollama doesn't support vision - ignoring image");
            // We could return failure here, but for now we'll just log a warning and continue with text-only
        }

        logger.LogInformation("🔵 START: Building system prompt...");
        var promptStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var systemPrompt = SystemPromptBuilder.BuildSystemPrompt(activeHabits, userTags, userFacts, routinePatterns: routinePatterns);
        promptStopwatch.Stop();
        logger.LogInformation("✅ System prompt built in {ElapsedMs}ms (length: {Length} chars)",
            promptStopwatch.ElapsedMilliseconds, systemPrompt.Length);

        var request = new OllamaRequest(
            _settings.Model,
            [
                new OllamaMessage("system", systemPrompt),
                new OllamaMessage("user", userMessage)
            ],
            Stream: false,
            Format: "json");

        try
        {
            logger.LogInformation("🔵 Calling Ollama API (Model: {Model})...", _settings.Model);
            var ollamaStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var response = await httpClient.PostAsJsonAsync("api/chat", request, cancellationToken);

            ollamaStopwatch.Stop();
            logger.LogInformation("✅ Ollama API responded in {ElapsedMs}ms", ollamaStopwatch.ElapsedMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Ollama API returned {Status}: {Body}", response.StatusCode, errorBody);
                return Result.Failure<AiActionPlan>($"Ollama API error: {response.StatusCode}");
            }

            logger.LogInformation("🔵 Deserializing Ollama response...");
            var deserializeStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken);
            var text = ollamaResponse?.Message?.Content;

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiActionPlan>("Ollama returned an empty response.");

            // Strip markdown code blocks if present
            text = text.Trim();
            if (text.StartsWith("```json"))
                text = text.Substring(7);
            else if (text.StartsWith("```"))
                text = text.Substring(3);

            if (text.EndsWith("```"))
                text = text.Substring(0, text.Length - 3);

            text = text.Trim();

            logger.LogInformation("Cleaned JSON (length: {Length} chars)", text.Length);

            var plan = JsonSerializer.Deserialize<AiActionPlan>(text, ActionPlanJsonOptions);

            if (plan is null)
            {
                logger.LogError("Deserialization returned null for text: {Text}", text);
                return Result.Failure<AiActionPlan>("Failed to deserialize AI response.");
            }

            deserializeStopwatch.Stop();
            logger.LogInformation("✅ Deserialization completed in {ElapsedMs}ms", deserializeStopwatch.ElapsedMilliseconds);

            totalStopwatch.Stop();
            logger.LogInformation("🎯 TOTAL InterpretAsync time: {ElapsedMs}ms", totalStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   ├─ Prompt build: {PromptMs}ms", promptStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   ├─ Ollama call: {OllamaMs}ms", ollamaStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   └─ Deserialize: {DeserializeMs}ms", deserializeStopwatch.ElapsedMilliseconds);

            return Result.Success(plan);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize Ollama response");
            return Result.Failure<AiActionPlan>($"Failed to parse AI response: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ollama API call failed");
            return Result.Failure<AiActionPlan>($"AI service error: {ex.Message}");
        }
    }

    // --- Ollama API DTOs ---

    private record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] OllamaMessage[] Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format);

    private record OllamaMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record OllamaResponse(
        [property: JsonPropertyName("message")] OllamaResponseMessage? Message,
        [property: JsonPropertyName("done")] bool Done);

    private record OllamaResponseMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string? Content);
}
