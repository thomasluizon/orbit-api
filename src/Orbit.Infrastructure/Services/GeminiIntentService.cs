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

    /// <summary>
    /// Conversation state maintained between SendWithToolsAsync and ContinueWithToolResultsAsync calls.
    /// </summary>
    private List<GeminiContent>? _conversationContents;
    private GeminiContent? _conversationSystemInstruction;
    private GeminiTool[]? _conversationTools;
    private GeminiToolConfig? _conversationToolConfig;

    // ───────────────────────────────────────────────────────────────
    //  Legacy method -- kept for backward compatibility
    // ───────────────────────────────────────────────────────────────

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

        logger.LogInformation("START: Building system prompt...");
        var promptStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var systemPrompt = SystemPromptBuilder.BuildSystemPrompt(
            activeHabits, userFacts,
            hasImage: imageData != null,
            routinePatterns: routinePatterns,
            userTags: userTags,
            userToday: userToday,
            habitMetrics: habitMetrics);
        promptStopwatch.Stop();
        logger.LogInformation("System prompt built in {ElapsedMs}ms (length: {Length} chars)",
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
        var currentParts = new List<GeminiPart> { new() { Text = userMessage } };

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
                MaxOutputTokens = 8192,
                ResponseMimeType = "application/json"
            }
        };

        try
        {
            logger.LogInformation("Calling Gemini API (Model: {Model})...", _settings.Model);
            var apiStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

            var response = await SendWithRetryAsync(url, request, cancellationToken);

            apiStopwatch.Stop();
            logger.LogInformation("Gemini API responded in {ElapsedMs}ms", apiStopwatch.ElapsedMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Gemini API returned {Status}: {Body}", response.StatusCode, errorBody);
                return Result.Failure<AiActionPlan>($"Gemini API error: {response.StatusCode}");
            }

            logger.LogInformation("Deserializing Gemini response...");
            var deserializeStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);
            var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiActionPlan>("Gemini returned an empty response.");

            logger.LogInformation("Gemini response (length: {Length} chars)", text.Length);
            logger.LogInformation("GEMINI RAW JSON: {Json}", text);

            // Fix invalid JSON escape sequences (e.g., \P, \C) that Gemini may generate
            text = Regex.Replace(text, @"\\([^""\\\/bfnrtu])", "$1");

            var plan = JsonSerializer.Deserialize<AiActionPlan>(text, ActionPlanJsonOptions);

            if (plan is null)
            {
                logger.LogError("Deserialization returned null for text: {Text}", text);
                return Result.Failure<AiActionPlan>("Failed to deserialize AI response.");
            }

            logger.LogInformation("Deserialized {ActionCount} actions: {ActionTypes}",
                plan.Actions.Count,
                string.Join(", ", plan.Actions.Select(a => a.Type.ToString())));

            deserializeStopwatch.Stop();

            totalStopwatch.Stop();
            logger.LogInformation("TOTAL InterpretAsync time: {ElapsedMs}ms", totalStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   Prompt build: {PromptMs}ms", promptStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   Gemini call: {GeminiMs}ms", apiStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   Deserialize: {DeserializeMs}ms", deserializeStopwatch.ElapsedMilliseconds);

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

    // ───────────────────────────────────────────────────────────────
    //  New function-calling methods
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
        var contents = new List<GeminiContent>();

        // System instruction (separate from conversation - proper Gemini API field)
        var systemInstruction = new GeminiContent { Parts = [new GeminiPart { Text = systemPrompt }] };
        _conversationSystemInstruction = systemInstruction;

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
        var currentParts = new List<GeminiPart> { new() { Text = userMessage } };

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

        // Build tools
        var tools = new GeminiTool[]
        {
            new() { FunctionDeclarations = toolDeclarations.ToArray() }
        };

        var toolConfig = new GeminiToolConfig
        {
            FunctionCallingConfig = new GeminiFunctionCallingConfig { Mode = "AUTO" }
        };

        // Store conversation state for ContinueWithToolResultsAsync
        _conversationContents = contents;
        _conversationTools = tools;
        _conversationToolConfig = toolConfig;

        return await CallGeminiWithToolsAsync(cancellationToken);
    }

    public async Task<Result<AiResponse>> ContinueWithToolResultsAsync(
        IReadOnlyList<AiToolCallResult> results,
        CancellationToken cancellationToken = default)
    {
        if (_conversationContents is null)
            return Result.Failure<AiResponse>("No active conversation. Call SendWithToolsAsync first.");

        // Build functionResponse parts for all results
        var responseParts = results.Select(r => new GeminiPart
        {
            FunctionResponse = new GeminiFunctionResponse
            {
                Name = r.Name,
                Response = BuildFunctionResponsePayload(r)
            }
        }).ToArray();

        _conversationContents.Add(new GeminiContent { Role = "user", Parts = responseParts });

        return await CallGeminiWithToolsAsync(cancellationToken);
    }

    // ───────────────────────────────────────────────────────────────
    //  Shared helpers
    // ───────────────────────────────────────────────────────────────

    private async Task<Result<AiResponse>> CallGeminiWithToolsAsync(CancellationToken cancellationToken)
    {
        var request = new GeminiRequest
        {
            SystemInstruction = _conversationSystemInstruction,
            Contents = _conversationContents!.ToArray(),
            GenerationConfig = new GeminiGenerationConfig { Temperature = 0.1, MaxOutputTokens = 8192 },
            Tools = _conversationTools,
            ToolConfig = _conversationToolConfig
        };

        try
        {
            var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

            logger.LogInformation("Calling Gemini API with tools (Model: {Model})...", _settings.Model);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var response = await SendWithRetryAsync(url, request, cancellationToken);

            stopwatch.Stop();
            logger.LogInformation("Gemini API responded in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Gemini API returned {Status}: {Body}", response.StatusCode, errorBody);
                return Result.Failure<AiResponse>($"Gemini API error: {response.StatusCode}");
            }

            var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);
            var candidateContent = geminiResponse?.Candidates?.FirstOrDefault()?.Content;

            if (candidateContent?.Parts is null or { Length: 0 })
                return Result.Failure<AiResponse>("Gemini returned an empty response.");

            // Append model response to conversation state
            _conversationContents!.Add(candidateContent);

            // Check if response contains function calls
            var functionCalls = candidateContent.Parts
                .Where(p => p.FunctionCall is not null)
                .Select((p, i) => new AiToolCall(
                    p.FunctionCall!.Name,
                    $"call_{i}",
                    p.FunctionCall.Args))
                .ToList();

            if (functionCalls.Count > 0)
            {
                logger.LogInformation("Gemini returned {Count} function call(s): {Names}",
                    functionCalls.Count,
                    string.Join(", ", functionCalls.Select(fc => fc.Name)));

                return Result.Success(new AiResponse { ToolCalls = functionCalls });
            }

            // Otherwise it's a text response
            var text = string.Join("", candidateContent.Parts
                .Where(p => p.Text is not null)
                .Select(p => p.Text));

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiResponse>("Gemini returned neither function calls nor text.");

            logger.LogInformation("Gemini returned text response (length: {Length} chars)", text.Length);

            return Result.Success(new AiResponse { TextMessage = text });
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize Gemini function-calling response");
            return Result.Failure<AiResponse>($"Failed to parse AI response: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Gemini API call failed");
            return Result.Failure<AiResponse>($"AI service error: {ex.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string url,
        GeminiRequest request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        int retryCount = 0;
        const int maxRetries = 3;

        while (retryCount <= maxRetries)
        {
            response = await httpClient.PostAsJsonAsync(url, request, cancellationToken);

            if (response.IsSuccessStatusCode || response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                break;

            retryCount++;
            if (retryCount <= maxRetries)
            {
                var delayMs = (int)Math.Pow(2, retryCount) * 1000; // Exponential backoff: 2s, 4s, 8s
                logger.LogWarning("Rate limited. Retrying in {DelayMs}ms (attempt {Retry}/{Max})...",
                    delayMs, retryCount, maxRetries);
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        return response!;
    }

    private static object BuildFunctionResponsePayload(AiToolCallResult result)
    {
        var payload = new Dictionary<string, object> { ["success"] = result.Success };

        if (result.EntityId is not null)
            payload["entity_id"] = result.EntityId;

        if (result.EntityName is not null)
            payload["entity_name"] = result.EntityName;

        if (result.Error is not null)
            payload["error"] = result.Error;

        return payload;
    }

    // ───────────────────────────────────────────────────────────────
    //  Gemini API DTOs
    // ───────────────────────────────────────────────────────────────

    private record GeminiRequest
    {
        [JsonPropertyName("system_instruction")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GeminiContent? SystemInstruction { get; init; }

        [JsonPropertyName("contents")]
        public GeminiContent[] Contents { get; init; } = [];

        [JsonPropertyName("generationConfig")]
        public GeminiGenerationConfig? GenerationConfig { get; init; }

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GeminiTool[]? Tools { get; init; }

        [JsonPropertyName("tool_config")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GeminiToolConfig? ToolConfig { get; init; }
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public InlineData? InlineData { get; init; }

        [JsonPropertyName("functionCall")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GeminiFunctionCall? FunctionCall { get; init; }

        [JsonPropertyName("functionResponse")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GeminiFunctionResponse? FunctionResponse { get; init; }
    }

    private record InlineData
    {
        [JsonPropertyName("mime_type")]
        public string MimeType { get; init; } = string.Empty;

        [JsonPropertyName("data")]
        public string Data { get; init; } = string.Empty;
    }

    private record GeminiFunctionCall
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("args")]
        public JsonElement Args { get; init; }
    }

    private record GeminiFunctionResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("response")]
        public object Response { get; init; } = new();
    }

    private record GeminiTool
    {
        [JsonPropertyName("function_declarations")]
        public object[] FunctionDeclarations { get; init; } = [];
    }

    private record GeminiToolConfig
    {
        [JsonPropertyName("function_calling_config")]
        public GeminiFunctionCallingConfig FunctionCallingConfig { get; init; } = new();
    }

    private record GeminiFunctionCallingConfig
    {
        [JsonPropertyName("mode")]
        public string Mode { get; init; } = "AUTO";
    }

    private record GeminiGenerationConfig
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("maxOutputTokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? MaxOutputTokens { get; init; }

        [JsonPropertyName("responseMimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ResponseMimeType { get; init; }
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
