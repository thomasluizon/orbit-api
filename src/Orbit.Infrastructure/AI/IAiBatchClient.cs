namespace Orbit.Infrastructure.AI;

/// <summary>
/// Abstraction over the OpenAI Batch API calls the fact-extraction flow needs: upload a JSONL
/// request file, create a 24h chat-completions batch, poll a batch's status across ticks, and
/// download/delete the hosted input/output files. Implemented by <see cref="AiBatchClient"/>.
/// </summary>
public interface IAiBatchClient
{
    Task<string> UploadJsonlAsync(string content, CancellationToken cancellationToken = default);

    Task<string> CreateChatCompletionsBatchAsync(string inputFileId, CancellationToken cancellationToken = default);

    Task<BatchStatusResult> GetBatchAsync(string batchId, CancellationToken cancellationToken = default);

    Task<string> DownloadFileAsync(string fileId, CancellationToken cancellationToken = default);

    Task DeleteFileAsync(string fileId, CancellationToken cancellationToken = default);
}

public readonly record struct BatchStatusResult(string Status, string? OutputFileId, string? ErrorFileId);
