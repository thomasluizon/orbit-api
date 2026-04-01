using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

public sealed class AiCompletionClient
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<AiCompletionClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AiCompletionClient(IOptions<AiSettings> options, ILogger<AiCompletionClient> logger)
    {
        _logger = logger;
        var settings = options.Value;

        _chatClient = new ChatClient(
            model: settings.Model,
            credential: new ApiKeyCredential(settings.ApiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(settings.BaseUrl)
            });
    }

    /// <summary>
    /// Direct access to the underlying ChatClient for advanced scenarios (tool calling, multi-turn).
    /// </summary>
    public ChatClient ChatClient => _chatClient;

    // ───────────────────────────────────────────────────────────────
    //  Simple text completion
    // ───────────────────────────────────────────────────────────────

    public async Task<string?> CompleteTextAsync(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.7,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = (float)temperature
        };

        _logger.LogInformation("Calling AI API for text completion...");

        var completion = await _chatClient.CompleteChatAsync(messages, options, cancellationToken);
        var text = completion.Value.Content.FirstOrDefault()?.Text;

        if (!string.IsNullOrWhiteSpace(text))
            _logger.LogInformation("AI text completion successful ({Length} chars)", text.Length);

        return text;
    }

    // ───────────────────────────────────────────────────────────────
    //  JSON-mode completion (structured output)
    // ───────────────────────────────────────────────────────────────

    public async Task<T?> CompleteJsonAsync<T>(
        string prompt,
        double temperature = 0.1,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are a helpful assistant. Respond only with valid JSON."),
            new UserChatMessage(prompt)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = (float)temperature,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        _logger.LogInformation("Calling AI API for JSON completion...");

        var completion = await _chatClient.CompleteChatAsync(messages, options, cancellationToken);
        var text = completion.Value.Content.FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("AI returned empty response for JSON completion");
            return default;
        }

        _logger.LogInformation("AI JSON completion successful ({Length} chars)", text.Length);

        return JsonSerializer.Deserialize<T>(text, JsonOptions);
    }
}
