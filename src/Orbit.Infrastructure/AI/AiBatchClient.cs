#pragma warning disable OPENAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Batch;
using OpenAI.Files;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

/// <summary>
/// Thin wrapper over the OpenAI .NET SDK's <see cref="OpenAIFileClient"/> and
/// <see cref="BatchClient"/> for the fact-extraction Batch API flow. Constructed from
/// <see cref="AiSettings"/>, mirroring <see cref="AiCompletionClient"/>'s endpoint / credential /
/// retry configuration but using the longer <see cref="AiSettings.BatchNetworkTimeoutSeconds"/> for
/// whole-file up/download. The Batch + Files surface is pre-release (OPENAI001), suppressed
/// file-wide here so the experimental API stays contained to this adapter.
/// </summary>
public sealed class AiBatchClient : IAiBatchClient
{
    private const string ChatCompletionsEndpoint = "/v1/chat/completions";
    private const string CompletionWindow = "24h";

    private readonly OpenAIFileClient _fileClient;
    private readonly BatchClient _batchClient;

    public AiBatchClient(IOptions<AiSettings> options, ILogger<AiBatchClient> logger)
    {
        var settings = options.Value;

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(settings.BaseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(settings.BatchNetworkTimeoutSeconds),
            RetryPolicy = new AiRetryLoggingPolicy(settings.MaxRetries, logger)
        };
        var credential = new ApiKeyCredential(settings.ApiKey);

        _fileClient = new OpenAIFileClient(credential, clientOptions);
        _batchClient = new BatchClient(credential, clientOptions);
    }

    public async Task<string> UploadJsonlAsync(string content, CancellationToken cancellationToken = default)
    {
        var bytes = BinaryData.FromBytes(Encoding.UTF8.GetBytes(content));
        var result = await _fileClient.UploadFileAsync(bytes, "fact-extraction.jsonl", FileUploadPurpose.Batch);
        return result.Value.Id;
    }

    public async Task<string> CreateChatCompletionsBatchAsync(string inputFileId, CancellationToken cancellationToken = default)
    {
        var requestBody = BinaryContent.Create(BinaryData.FromObjectAsJson(new
        {
            input_file_id = inputFileId,
            endpoint = ChatCompletionsEndpoint,
            completion_window = CompletionWindow
        }));

        var operation = await _batchClient.CreateBatchAsync(requestBody, waitUntilCompleted: false);
        return operation.BatchId;
    }

    public async Task<BatchStatusResult> GetBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        var result = await _batchClient.GetBatchAsync(batchId, options: null);
        using var document = JsonDocument.Parse(result.GetRawResponse().Content.ToMemory());
        var root = document.RootElement;

        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? string.Empty : string.Empty;
        var outputFileId = root.TryGetProperty("output_file_id", out var outputElement) ? outputElement.GetString() : null;
        var errorFileId = root.TryGetProperty("error_file_id", out var errorElement) ? errorElement.GetString() : null;

        return new BatchStatusResult(status, outputFileId, errorFileId);
    }

    public async Task<string> DownloadFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var result = await _fileClient.DownloadFileAsync(fileId, cancellationToken);
        return result.Value.ToString();
    }

    public async Task DeleteFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        await _fileClient.DeleteFileAsync(fileId, cancellationToken);
    }
}
