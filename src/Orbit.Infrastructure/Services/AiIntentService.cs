using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed class AiIntentService(
    AiCompletionClient aiClient,
    ILogger<AiIntentService> logger) : IAiIntentService
{
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

    /// <summary>
    /// Conversation state maintained between SendWithToolsAsync and ContinueWithToolResultsAsync calls.
    /// </summary>
    private List<ChatMessage>? _conversationMessages;
    private ChatCompletionOptions? _conversationOptions;

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

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        // Conversation history
        if (history is { Count: > 0 })
        {
            foreach (var msg in history)
            {
                if (msg.Role == "user")
                    messages.Add(new UserChatMessage(msg.Content));
                else
                    messages.Add(new AssistantChatMessage(msg.Content));
            }
        }

        // Current user message with optional image
        if (imageData != null && !string.IsNullOrWhiteSpace(imageMimeType))
        {
            messages.Add(new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(userMessage),
                ChatMessageContentPart.CreateImagePart(
                    BinaryData.FromBytes(imageData), imageMimeType)));
        }
        else
        {
            messages.Add(new UserChatMessage(userMessage));
        }

        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,
            MaxOutputTokenCount = 8192,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        try
        {
            logger.LogInformation("Calling AI API (legacy InterpretAsync)...");
            var apiStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var completion = await aiClient.ChatClient.CompleteChatAsync(messages, options, cancellationToken);

            apiStopwatch.Stop();
            logger.LogInformation("AI API responded in {ElapsedMs}ms", apiStopwatch.ElapsedMilliseconds);

            var text = completion.Value.Content.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiActionPlan>("AI returned an empty response.");

            logger.LogInformation("AI response (length: {Length} chars)", text.Length);
            logger.LogInformation("AI RAW JSON: {Json}", text);

            // Fix invalid JSON escape sequences that AI may generate
            text = Regex.Replace(text, @"\\([^""\\\/bfnrtu])", "$1");

            var deserializeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var plan = JsonSerializer.Deserialize<AiActionPlan>(text, ActionPlanJsonOptions);
            deserializeStopwatch.Stop();

            if (plan is null)
            {
                logger.LogError("Deserialization returned null for text: {Text}", text);
                return Result.Failure<AiActionPlan>("Failed to deserialize AI response.");
            }

            logger.LogInformation("Deserialized {ActionCount} actions: {ActionTypes}",
                plan.Actions.Count,
                string.Join(", ", plan.Actions.Select(a => a.Type.ToString())));

            totalStopwatch.Stop();
            logger.LogInformation("TOTAL InterpretAsync time: {ElapsedMs}ms", totalStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   Prompt build: {PromptMs}ms", promptStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   AI call: {AiMs}ms", apiStopwatch.ElapsedMilliseconds);
            logger.LogInformation("   Deserialize: {DeserializeMs}ms", deserializeStopwatch.ElapsedMilliseconds);

            return Result.Success(plan);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize AI response");
            return Result.Failure<AiActionPlan>($"Failed to parse AI response: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI API call failed");
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
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        // Conversation history
        if (history is { Count: > 0 })
        {
            foreach (var msg in history)
            {
                if (msg.Role == "user")
                    messages.Add(new UserChatMessage(msg.Content));
                else
                    messages.Add(new AssistantChatMessage(msg.Content));
            }
        }

        // Current user message with optional image
        if (imageData != null && !string.IsNullOrWhiteSpace(imageMimeType))
        {
            messages.Add(new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(userMessage),
                ChatMessageContentPart.CreateImagePart(
                    BinaryData.FromBytes(imageData), imageMimeType)));
        }
        else
        {
            messages.Add(new UserChatMessage(userMessage));
        }

        // Convert tool declarations to SDK ChatTool instances
        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,
            MaxOutputTokenCount = 8192
        };

        foreach (var decl in toolDeclarations)
        {
            var tool = ConvertToSdkTool(decl);
            if (tool is not null)
                options.Tools.Add(tool);
        }

        // Store conversation state
        _conversationMessages = messages;
        _conversationOptions = options;

        return await CallWithToolsAsync(cancellationToken);
    }

    public async Task<Result<AiResponse>> ContinueWithToolResultsAsync(
        IReadOnlyList<AiToolCallResult> results,
        CancellationToken cancellationToken = default)
    {
        if (_conversationMessages is null)
            return Result.Failure<AiResponse>("No active conversation. Call SendWithToolsAsync first.");

        // Add tool result messages (one per result, with tool_call_id)
        foreach (var result in results)
        {
            var payload = new Dictionary<string, object> { ["success"] = result.Success };
            if (result.EntityId is not null) payload["entity_id"] = result.EntityId;
            if (result.EntityName is not null) payload["entity_name"] = result.EntityName;
            if (result.Error is not null) payload["error"] = result.Error;

            _conversationMessages.Add(new ToolChatMessage(result.Id, JsonSerializer.Serialize(payload)));
        }

        return await CallWithToolsAsync(cancellationToken);
    }

    // ───────────────────────────────────────────────────────────────
    //  Shared helpers
    // ───────────────────────────────────────────────────────────────

    private async Task<Result<AiResponse>> CallWithToolsAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Calling AI API with tools...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var completion = await aiClient.ChatClient.CompleteChatAsync(
                _conversationMessages!, _conversationOptions!, cancellationToken);

            stopwatch.Stop();
            logger.LogInformation("AI API responded in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

            var result = completion.Value;

            // Append the assistant message to conversation state
            _conversationMessages!.Add(new AssistantChatMessage(result));

            // Check if response contains tool calls
            if (result.FinishReason == ChatFinishReason.ToolCalls && result.ToolCalls.Count > 0)
            {
                var toolCalls = result.ToolCalls
                    .Select(tc =>
                    {
                        var args = JsonDocument.Parse(tc.FunctionArguments).RootElement;
                        return new AiToolCall(tc.FunctionName, tc.Id, args);
                    })
                    .ToList();

                logger.LogInformation("AI returned {Count} tool call(s): {Names}",
                    toolCalls.Count,
                    string.Join(", ", toolCalls.Select(tc => tc.Name)));

                return Result.Success(new AiResponse { ToolCalls = toolCalls });
            }

            // Otherwise it's a text response
            var text = result.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiResponse>("AI returned neither tool calls nor text.");

            logger.LogInformation("AI returned text response (length: {Length} chars)", text.Length);
            return Result.Success(new AiResponse { TextMessage = text });
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize AI function-calling response");
            return Result.Failure<AiResponse>($"Failed to parse AI response: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI API call failed");
            return Result.Failure<AiResponse>($"AI service error: {ex.Message}");
        }
    }

    private static ChatTool? ConvertToSdkTool(object declaration)
    {
        var json = JsonSerializer.Serialize(declaration, SerializeOptions);
        // Normalize uppercase types (OBJECT, STRING, etc.) to JSON Schema lowercase
        json = NormalizeSchemaTypes(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.GetProperty("name").GetString() ?? "";
        var description = root.TryGetProperty("description", out var descEl) ? descEl.GetString() : null;

        BinaryData? parameters = null;
        if (root.TryGetProperty("parameters", out var paramsEl))
            parameters = BinaryData.FromString(paramsEl.GetRawText());

        return ChatTool.CreateFunctionTool(name, description, parameters);
    }

    private static string NormalizeSchemaTypes(string json)
    {
        return Regex.Replace(json, @"""type""\s*:\s*""(OBJECT|STRING|ARRAY|NUMBER|BOOLEAN|INTEGER)""",
            m => $@"""type"":""{m.Groups[1].Value.ToLowerInvariant()}""");
    }
}
