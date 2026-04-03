using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;

#pragma warning disable CA1873

namespace Orbit.Infrastructure.Services;

public sealed partial class AiIntentService(
    AiCompletionClient aiClient,
    ILogger<AiIntentService> logger) : IAiIntentService
{
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
            LogCallingAiWithTools(logger);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var completion = await aiClient.ChatClient.CompleteChatAsync(
                _conversationMessages!, _conversationOptions!, cancellationToken);

            stopwatch.Stop();
            LogAiApiResponded(logger, stopwatch.ElapsedMilliseconds);

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

                LogAiReturnedToolCalls(logger, toolCalls.Count,
                    string.Join(", ", toolCalls.Select(tc => tc.Name)));

                return Result.Success(new AiResponse { ToolCalls = toolCalls });
            }

            // Otherwise it's a text response
            var text = result.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiResponse>("AI returned neither tool calls nor text.");

            LogAiReturnedTextResponse(logger, text.Length);
            return Result.Success(new AiResponse { TextMessage = text });
        }
        catch (JsonException ex)
        {
            LogAiDeserializationFailed(logger, ex);
            return Result.Failure<AiResponse>("AI service temporarily unavailable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAiApiCallFailed(logger, ex);
            return Result.Failure<AiResponse>("AI service temporarily unavailable");
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

    [GeneratedRegex(@"""type""\s*:\s*""(OBJECT|STRING|ARRAY|NUMBER|BOOLEAN|INTEGER)""")]
    private static partial Regex SchemaTypeRegex();

    private static string NormalizeSchemaTypes(string json)
    {
        return SchemaTypeRegex().Replace(json,
            m => $@"""type"":""{m.Groups[1].Value.ToLowerInvariant()}""");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Calling AI API with tools...")]
    private static partial void LogCallingAiWithTools(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "AI API responded in {ElapsedMs}ms")]
    private static partial void LogAiApiResponded(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "AI returned {Count} tool call(s): {Names}")]
    private static partial void LogAiReturnedToolCalls(ILogger logger, int count, string names);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "AI returned text response (length: {Length} chars)")]
    private static partial void LogAiReturnedTextResponse(ILogger logger, int length);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Failed to deserialize AI function-calling response")]
    private static partial void LogAiDeserializationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "AI API call failed")]
    private static partial void LogAiApiCallFailed(ILogger logger, Exception ex);

}
