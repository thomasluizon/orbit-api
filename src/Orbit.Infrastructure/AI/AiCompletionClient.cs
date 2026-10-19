#pragma warning disable OPENAI001

using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

public sealed partial class AiCompletionClient
{
    private readonly ResponsesClient _responsesClient;
    internal TimeSpan CompletionTimeout { get; }
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
    {
        _logger = logger;
        _usageRecorder = usageRecorder;
        var settings = options.Value;
        CompletionTimeout = settings.CompletionTimeout;

        var clientOptions = new ResponsesClientOptions
        {
            Endpoint = new Uri(settings.BaseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(settings.NetworkTimeoutSeconds),
            RetryPolicy = new AiRetryLoggingPolicy(settings.MaxRetries, logger)
        };
        var credential = new ApiKeyCredential(settings.ApiKey);

        _primaryModel = settings.Model;
        _responsesClient = new ResponsesClient(credential, clientOptions);

        _subTaskModel = ResolveSubTaskModel(settings.SubTaskModel, settings.Model);
    }

    internal static string ResolveSubTaskModel(string subTaskModel, string primaryModel)
        => string.IsNullOrWhiteSpace(subTaskModel) ? primaryModel : subTaskModel;

    internal static bool ShouldApplyTemperature(AiModelTier tier, string primaryModel, string subTaskModel)
        => !IsReasoningModel(tier == AiModelTier.SubTask ? subTaskModel : primaryModel);

    private static bool IsReasoningModel(string model)
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
        string primaryModel = "gpt-4.1-mini",
        string subTaskModel = "gpt-6-luna")
    {
        CompletionTimeout = completionTimeout ?? new AiSettings().CompletionTimeout;
        _responsesClient = responsesClient;
        _primaryModel = primaryModel;
        _subTaskModel = ResolveSubTaskModel(subTaskModel, primaryModel);
        _logger = logger;
        _usageRecorder = usageRecorder;
    }

    internal ResponsesClient ResponsesClient => _responsesClient;

    internal string ChatModel => _primaryModel;

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
            options.ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = ResponseReasoningEffortLevel.Low
            };

        options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        if (userId is { } id && id != Guid.Empty)
        {
            options.EndUserId = id.ToString("N");
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

        var completion = await _responsesClient.CreateResponseAsync(options, cancellationToken);
        await RecordUsageAsync(completion.Value.Usage, purpose, options.Model, cancellationToken);
        EnsureSuccessful(completion.Value);
        var text = completion.Value.GetOutputText();

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
        CancellationToken cancellationToken = default)
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

        var completion = await _responsesClient.CreateResponseAsync(options, cancellationToken);
        await RecordUsageAsync(completion.Value.Usage, purpose, options.Model, cancellationToken, userId);
        EnsureSuccessful(completion.Value);
        var text = completion.Value.GetOutputText();

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

    internal CancellationTokenSource CreateCompletionCancellation(CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(CompletionTimeout);
        return cancellation;
    }

    private async Task RecordUsageAsync(
        ResponseTokenUsage? usage,
        string purpose,
        string model,
        CancellationToken cancellationToken,
        Guid? userId = null)
    {
        if (usage is null)
            return;

        var cachedTokens = usage.InputTokenDetails?.CachedTokenCount ?? 0;

        LogAiTokenUsage(
            _logger,
            purpose,
            model,
            cachedTokens,
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount);

        await _usageRecorder.RecordAsync(
            purpose,
            model,
            cachedTokens,
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount,
            cancellationToken,
            userId);
    }

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
