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

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Conversation state for multi-turn tool calling
    private List<OllamaMessage>? _conversationMessages;
    private List<OllamaTool>? _conversationTools;

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

        if (imageData != null)
            logger.LogWarning("Image data provided but Ollama doesn't support vision - ignoring image");

        logger.LogInformation("Building system prompt...");
        var promptStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var systemPrompt = SystemPromptBuilder.BuildSystemPrompt(activeHabits, userFacts, routinePatterns: routinePatterns, userTags: userTags, userToday: userToday, habitMetrics: habitMetrics);
        promptStopwatch.Stop();
        logger.LogInformation("System prompt built in {ElapsedMs}ms (length: {Length} chars)",
            promptStopwatch.ElapsedMilliseconds, systemPrompt.Length);

        var request = new OllamaLegacyRequest(
            _settings.Model,
            [
                new OllamaMessage { Role = "system", Content = systemPrompt },
                new OllamaMessage { Role = "user", Content = userMessage }
            ],
            Stream: false,
            Format: "json");

        try
        {
            logger.LogInformation("Calling Ollama API (Model: {Model})...", _settings.Model);
            var ollamaStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var response = await httpClient.PostAsJsonAsync("api/chat", request, SerializeOptions, cancellationToken);

            ollamaStopwatch.Stop();
            logger.LogInformation("Ollama API responded in {ElapsedMs}ms", ollamaStopwatch.ElapsedMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Ollama API returned {Status}: {Body}", response.StatusCode, errorBody);
                return Result.Failure<AiActionPlan>($"Ollama API error: {response.StatusCode}");
            }

            var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken);
            var text = ollamaResponse?.Message?.Content;

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiActionPlan>("Ollama returned an empty response.");

            text = text.Trim();
            if (text.StartsWith("```json"))
                text = text[7..];
            else if (text.StartsWith("```"))
                text = text[3..];

            if (text.EndsWith("```"))
                text = text[..^3];

            text = text.Trim();

            // Fix invalid JSON escape sequences (e.g., \P, \C) that AI may generate
            text = Regex.Replace(text, @"\\([^""\\\/bfnrtu])", "$1");

            var plan = JsonSerializer.Deserialize<AiActionPlan>(text, ActionPlanJsonOptions);

            if (plan is null)
            {
                logger.LogError("Deserialization returned null for text: {Text}", text);
                return Result.Failure<AiActionPlan>("Failed to deserialize AI response.");
            }

            totalStopwatch.Stop();
            logger.LogInformation("TOTAL InterpretAsync time: {ElapsedMs}ms", totalStopwatch.ElapsedMilliseconds);

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

    // ───────────────────────────────────────────────────────────────
    //  Function-calling methods
    // ───────────────────────────────────────────────────────────────

    public async Task<Result<AiResponse>> SendWithToolsAsync(
        string userMessage,
        string systemPrompt,
        IReadOnlyList<object> toolDeclarations,
        byte[]? imageData = null,
        string? imageMimeType = null,
        IReadOnlyList<ChatHistoryMessage>? history = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<OllamaMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };

        if (history is { Count: > 0 })
        {
            foreach (var msg in history)
            {
                messages.Add(new OllamaMessage
                {
                    Role = msg.Role == "user" ? "user" : "assistant",
                    Content = msg.Content
                });
            }
        }

        messages.Add(new OllamaMessage { Role = "user", Content = userMessage });

        // Convert tool declarations to Ollama format
        var tools = ConvertToolDeclarations(toolDeclarations);

        // Store conversation state for ContinueWithToolResultsAsync
        _conversationMessages = messages;
        _conversationTools = tools;

        return await CallOllamaWithToolsAsync(cancellationToken);
    }

    public async Task<Result<AiResponse>> ContinueWithToolResultsAsync(
        IReadOnlyList<AiToolCallResult> results,
        CancellationToken cancellationToken = default)
    {
        if (_conversationMessages is null)
            return Result.Failure<AiResponse>("No active conversation. Call SendWithToolsAsync first.");

        // Append a tool result message for each result
        foreach (var result in results)
        {
            var payload = new Dictionary<string, object> { ["success"] = result.Success };
            if (result.EntityId is not null) payload["entity_id"] = result.EntityId;
            if (result.EntityName is not null) payload["entity_name"] = result.EntityName;
            if (result.Error is not null) payload["error"] = result.Error;

            _conversationMessages.Add(new OllamaMessage
            {
                Role = "tool",
                Content = JsonSerializer.Serialize(payload)
            });
        }

        return await CallOllamaWithToolsAsync(cancellationToken);
    }

    // ───────────────────────────────────────────────────────────────
    //  Shared helpers
    // ───────────────────────────────────────────────────────────────

    private async Task<Result<AiResponse>> CallOllamaWithToolsAsync(CancellationToken cancellationToken)
    {
        var request = new OllamaToolRequest
        {
            Model = _settings.Model,
            Messages = _conversationMessages!,
            Stream = false,
            Tools = _conversationTools
        };

        try
        {
            logger.LogInformation("Calling Ollama API with tools (Model: {Model})...", _settings.Model);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var response = await httpClient.PostAsJsonAsync("api/chat", request, SerializeOptions, cancellationToken);

            stopwatch.Stop();
            logger.LogInformation("Ollama API responded in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Ollama API returned {Status}: {Body}", response.StatusCode, errorBody);
                return Result.Failure<AiResponse>($"Ollama API error: {response.StatusCode}");
            }

            var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken);
            var message = ollamaResponse?.Message;

            if (message is null)
                return Result.Failure<AiResponse>("Ollama returned an empty response.");

            // Append the assistant message to conversation state (preserves tool_calls for context)
            _conversationMessages!.Add(new OllamaMessage
            {
                Role = "assistant",
                Content = message.Content ?? "",
                ToolCalls = message.ToolCalls
            });

            // Check if response contains tool calls
            if (message.ToolCalls is { Count: > 0 })
            {
                var toolCalls = message.ToolCalls
                    .Select((tc, i) => new AiToolCall(
                        tc.Function.Name,
                        $"call_{i}",
                        tc.Function.Arguments))
                    .ToList();

                logger.LogInformation("Ollama returned {Count} tool call(s): {Names}",
                    toolCalls.Count,
                    string.Join(", ", toolCalls.Select(tc => tc.Name)));

                return Result.Success(new AiResponse { ToolCalls = toolCalls });
            }

            // Otherwise it's a text response
            var text = message.Content;
            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiResponse>("Ollama returned neither tool calls nor text.");

            logger.LogInformation("Ollama returned text response (length: {Length} chars)", text.Length);

            return Result.Success(new AiResponse { TextMessage = text });
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize Ollama tool-calling response");
            return Result.Failure<AiResponse>($"Failed to parse AI response: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ollama API call failed");
            return Result.Failure<AiResponse>($"AI service error: {ex.Message}");
        }
    }

    private static List<OllamaTool> ConvertToolDeclarations(IReadOnlyList<object> toolDeclarations)
    {
        // toolDeclarations are anonymous objects with { name, description, parameters }
        // Serialize and re-parse to convert to Ollama's { type: "function", function: {...} } format
        // Also normalize Gemini-style uppercase types (OBJECT, STRING) to JSON Schema lowercase
        var tools = new List<OllamaTool>();

        foreach (var decl in toolDeclarations)
        {
            var json = JsonSerializer.Serialize(decl, SerializeOptions);
            json = NormalizeSchemaTypes(json);
            var functionDef = JsonSerializer.Deserialize<OllamaFunctionDef>(json);

            if (functionDef is not null)
            {
                tools.Add(new OllamaTool
                {
                    Type = "function",
                    Function = functionDef
                });
            }
        }

        return tools;
    }

    private static string NormalizeSchemaTypes(string json)
    {
        // Gemini uses uppercase type values; Ollama/JSON Schema expects lowercase
        return Regex.Replace(json, @"""type""\s*:\s*""(OBJECT|STRING|ARRAY|NUMBER|BOOLEAN|INTEGER)""",
            m => $@"""type"":""{m.Groups[1].Value.ToLowerInvariant()}""");
    }

    // ───────────────────────────────────────────────────────────────
    //  DTOs
    // ───────────────────────────────────────────────────────────────

    // Legacy request (InterpretAsync - JSON mode, no tools)
    private record OllamaLegacyRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] OllamaMessage[] Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format);

    // Tool-calling request
    private class OllamaToolRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public List<OllamaMessage> Messages { get; set; } = [];
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("tools")] public List<OllamaTool>? Tools { get; set; }
    }

    private class OllamaMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<OllamaToolCall>? ToolCalls { get; set; }
    }

    private class OllamaTool
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public OllamaFunctionDef Function { get; set; } = new();
    }

    private class OllamaFunctionDef
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("parameters")] public JsonElement? Parameters { get; set; }
    }

    private class OllamaToolCall
    {
        [JsonPropertyName("function")] public OllamaToolCallFunction Function { get; set; } = new();
    }

    private class OllamaToolCallFunction
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("arguments")] public JsonElement Arguments { get; set; }
    }

    // Response
    private record OllamaResponse(
        [property: JsonPropertyName("message")] OllamaResponseMessage? Message,
        [property: JsonPropertyName("done")] bool Done);

    private class OllamaResponseMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<OllamaToolCall>? ToolCalls { get; set; }
    }
}
