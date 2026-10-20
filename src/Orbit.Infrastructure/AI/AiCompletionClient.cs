#pragma warning disable OPENAI001

using System.ClientModel;
using System.Diagnostics;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

public sealed partial class AiCompletionClient
{
    private readonly ResponsesClient _responsesClient;
    private readonly ResponsesClient _streamingResponsesClient;
    internal TimeSpan CompletionTimeout { get; }
    internal TimeSpan StreamingCompletionTimeout { get; }
    private readonly string _primaryModel;
    private readonly string _subTaskModel;
    private readonly ILogger<AiCompletionClient> _logger;
    private readonly IAiUsageRecorder _usageRecorder;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AiCompletionClient(
        IOptions<AiSettings> options,
        ILogger<AiCompletionClient> logger,
        IAiUsageRecorder usageRecorder)
        : this(options, logger, usageRecorder, null)
    {
    }

    internal AiCompletionClient(
        IOptions<AiSettings> options,
        ILogger<AiCompletionClient> logger,
        IAiUsageRecorder usageRecorder,
        PipelineTransport? transport)
    {
        _logger = logger;
        _usageRecorder = usageRecorder;
        var settings = options.Value;
        CompletionTimeout = settings.CompletionTimeout;
        StreamingCompletionTimeout = settings.StreamingCompletionTimeout;

        ResponsesClient CreateClient(int networkTimeoutSeconds)
        {
            var clientOptions = new ResponsesClientOptions
            {
                Endpoint = new Uri(settings.BaseUrl),
                NetworkTimeout = TimeSpan.FromSeconds(networkTimeoutSeconds),
                Transport = transport,
                RetryPolicy = new AiRetryLoggingPolicy(settings.MaxRetries, logger)
            };
            clientOptions.AddPolicy(new AiBudgetDispatchPolicy(), PipelinePosition.BeforeTransport);
            return new ResponsesClient(new ApiKeyCredential(settings.ApiKey), clientOptions);
        }

        _primaryModel = settings.Model;
        _responsesClient = CreateClient(settings.BufferedNetworkTimeoutSeconds);
        _streamingResponsesClient = CreateClient(settings.NetworkTimeoutSeconds);

        _subTaskModel = ResolveSubTaskModel(settings.SubTaskModel, settings.Model);
    }

    internal static string ResolveSubTaskModel(string subTaskModel, string primaryModel)
        => string.IsNullOrWhiteSpace(subTaskModel) ? primaryModel : subTaskModel;

    internal static bool ShouldApplyTemperature(AiModelTier tier, string primaryModel, string subTaskModel)
        => !IsReasoningModel(tier == AiModelTier.SubTask ? subTaskModel : primaryModel);

    internal static bool IsReasoningModel(string model)
        => model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

    internal AiCompletionClient(
        ResponsesClient responsesClient,
        ILogger<AiCompletionClient> logger,
        IAiUsageRecorder usageRecorder,
        TimeSpan? completionTimeout = null,
        string primaryModel = "gpt-6-luna",
        string subTaskModel = "gpt-6-luna")
    {
        CompletionTimeout = completionTimeout ?? new AiSettings().CompletionTimeout;
        StreamingCompletionTimeout = completionTimeout ?? new AiSettings().StreamingCompletionTimeout;
        _responsesClient = responsesClient;
        _streamingResponsesClient = responsesClient;
        _primaryModel = primaryModel;
        _subTaskModel = ResolveSubTaskModel(subTaskModel, primaryModel);
        _logger = logger;
        _usageRecorder = usageRecorder;
    }

    internal ResponsesClient ResponsesClient => _responsesClient;
    internal ResponsesClient StreamingResponsesClient => _streamingResponsesClient;

    internal string ChatModel => _primaryModel;

    internal const int DefaultMaxOutputTokens = 8192;

    internal string SerializeTextRequest(string systemPrompt, string userPrompt, double temperature, int maxOutputTokens,
        AiModelTier tier, Guid? userId = null, bool json = false)
    {
        var options = CreateOptions(tier, temperature, maxOutputTokens, userId);
        options.InputItems.Add(ResponseItem.CreateSystemMessageItem(systemPrompt));
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(userPrompt));
        if (json)
            options.TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonObjectFormat() };
        return ModelReaderWriter.Write(options).ToString();
    }

    internal async Task<long> CountInputTokensAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var request = JsonDocument.Parse(requestJson);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "model", "input", "tools", "text", "reasoning", "instructions", "tool_choice",
            "parallel_tool_calls", "previous_response_id", "conversation", "truncation"
        };
        var fields = request.RootElement.EnumerateObject().Where(property => allowed.Contains(property.Name))
            .ToDictionary(property => property.Name, property => property.Value);
        using var content = BinaryContent.Create(BinaryData.FromObjectAsJson(fields));
        var result = await _responsesClient.GetInputTokenCountAsync(content, "application/json",
            new RequestOptions { CancellationToken = cancellationToken });
        using var response = JsonDocument.Parse(result.GetRawResponse().Content);
        var tokens = response.RootElement.GetProperty("input_tokens").GetInt64();
        if (tokens < 0)
            throw new InvalidOperationException("The provider returned an invalid input token count.");
        return tokens;
    }

    internal CreateResponseOptions CreateOptions(
        AiModelTier tier = AiModelTier.Primary,
        double temperature = 0.1,
        int? maxOutputTokens = null,
        Guid? userId = null)
    {
        var model = tier == AiModelTier.SubTask ? _subTaskModel : _primaryModel;
        var options = new CreateResponseOptions
        {
            Model = model,
            StoredOutputEnabled = false,
            MaxOutputTokenCount = maxOutputTokens
        };
        if (ShouldApplyTemperature(tier, _primaryModel, _subTaskModel))
            options.Temperature = (float)temperature;
        else
        {
            options.ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = ResponseReasoningEffortLevel.Low
            };
            options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        }

        if (userId is { } id && id != Guid.Empty)
        {
            options.SafetyIdentifier = id.ToString("N");
            options.PromptCacheKey = id.ToString("N");
        }
        return options;
    }

    public async Task<string?> CompleteTextAsync(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.7,
        int? maxOutputTokens = null,
        string purpose = "text",
        AiModelTier tier = AiModelTier.Primary,
        CancellationToken cancellationToken = default)
    {
        using var completionCancellation = CreateCompletionCancellation(cancellationToken);
        cancellationToken = completionCancellation.Token;
        var options = CreateOptions(tier, temperature, maxOutputTokens);
        options.InputItems.Add(ResponseItem.CreateSystemMessageItem(systemPrompt));
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(userPrompt));

        LogCallingTextCompletion(_logger);

        var response = await ExecuteResponseAsync(options, purpose, cancellationToken);
        EnsureSuccessful(response);
        var text = GetAssistantOutputText(response);

        if (!string.IsNullOrWhiteSpace(text))
            LogTextCompletionSuccessful(_logger, text.Length);

        return text;
    }

    public async Task<T?> CompleteJsonAsync<T>(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.1,
        int? maxOutputTokens = null,
        string purpose = "json",
        AiModelTier tier = AiModelTier.Primary,
        Guid? userId = null,
        CancellationToken cancellationToken = default,
        bool requireComplete = false)
    {
        using var completionCancellation = CreateCompletionCancellation(cancellationToken);
        cancellationToken = completionCancellation.Token;
        var options = CreateOptions(tier, temperature, maxOutputTokens, userId);
        options.InputItems.Add(ResponseItem.CreateSystemMessageItem(systemPrompt));
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(userPrompt));
        options.TextOptions = new ResponseTextOptions
        {
            TextFormat = ResponseTextFormat.CreateJsonObjectFormat()
        };

        LogCallingJsonCompletion(_logger);

        var response = await ExecuteResponseAsync(options, purpose, cancellationToken, userId);
        EnsureSuccessful(response);
        if (requireComplete && response.Status != ResponseStatus.Completed)
            throw new InvalidOperationException("AI date interpretation did not complete.");
        var text = GetAssistantOutputText(response);

        if (string.IsNullOrWhiteSpace(text))
        {
            LogEmptyJsonResponse(_logger);
            return default;
        }

        LogJsonCompletionSuccessful(_logger, text.Length);

        return JsonSerializer.Deserialize<T>(text, JsonOptions);
    }

    internal static void EnsureSuccessful(ResponseResult response)
    {
        if (response.Status is not (ResponseStatus.Completed or ResponseStatus.Incomplete))
            throw new InvalidOperationException("AI response did not complete successfully.");
    }

    internal static string GetAssistantOutputText(ResponseResult response)
        => string.Join("\n\n", response.OutputItems.OfType<MessageResponseItem>()
            .Where(message => message.Role == MessageRole.Assistant)
            .Select(message => string.Concat(message.Content
                .Where(part => part.Kind == ResponseContentPartKind.OutputText)
                .Select(part => part.Text)))
            .Where(text => !string.IsNullOrEmpty(text)));

    internal CancellationTokenSource CreateCompletionCancellation(CancellationToken cancellationToken, bool streaming = false)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(streaming ? StreamingCompletionTimeout : CompletionTimeout);
        return cancellation;
    }

    private async Task<ResponseResult> ExecuteResponseAsync(
        CreateResponseOptions options,
        string purpose,
        CancellationToken cancellationToken,
        Guid? userId = null)
    {
        var stopwatch = Stopwatch.StartNew();
        ResponseResult? response = null;
        var traceId = Guid.NewGuid().ToString("N");
        try
        {
            var completion = await _responsesClient.CreateResponseAsync(options, cancellationToken);
            response = completion.Value;
            return response;
        }
        finally
        {
            stopwatch.Stop();
            var usage = response?.Usage;
            var failed = response is null || response.Status is not (ResponseStatus.Completed or ResponseStatus.Incomplete);
            if (usage is not null)
                LogAiTokenUsage(_logger, purpose, options.Model, usage.InputTokenDetails?.CachedTokenCount ?? 0,
                    usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount);
            try
            {
                await _usageRecorder.RecordAsync(purpose, options.Model,
                    usage?.InputTokenDetails?.CachedTokenCount ?? 0,
                    usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0, usage?.TotalTokenCount ?? 0,
                    cancellationToken, userId,
                    new AiCallTelemetry(traceId, stopwatch.Elapsed.TotalSeconds,
                        failed,
                        [], HasUsage: usage is not null),
                    cacheWriteTokens: usage?.InputTokenDetails?.CacheWriteTokenCount ?? 0);
            }
            catch (Exception ex) when (failed || ex is not OperationCanceledException)
            {
                LogUsageRecordFailed(_logger, options.Model, purpose, ex);
            }
        }
    }

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Failed to record AI usage for {Model} ({Purpose})")]
    private static partial void LogUsageRecordFailed(ILogger logger, string model, string purpose, Exception ex);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Calling AI API for text completion...")]
    private static partial void LogCallingTextCompletion(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "AI text completion successful ({Length} chars)")]
    private static partial void LogTextCompletionSuccessful(ILogger logger, int length);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Calling AI API for JSON completion...")]
    private static partial void LogCallingJsonCompletion(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "AI returned empty response for JSON completion")]
    private static partial void LogEmptyJsonResponse(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "AI JSON completion successful ({Length} chars)")]
    private static partial void LogJsonCompletionSuccessful(ILogger logger, int length);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "AI token usage ({Purpose} via {Model}): cached={CachedTokens}, prompt={PromptTokens}, completion={CompletionTokens}, total={TotalTokens}")]
    private static partial void LogAiTokenUsage(ILogger logger, string purpose, string model, int cachedTokens, int promptTokens, int completionTokens, int totalTokens);

}
