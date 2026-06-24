using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface IFactExtractionService
{
    /// <summary>
    /// Submits a fact-extraction request for one chat turn to the OpenAI Batch API (nano model, 24h
    /// SLA) and records an <see cref="AiFactExtractionBatch"/> tracking row. Best-effort: any failure
    /// is logged and swallowed so it never propagates into the chat request path. No conversation text
    /// is persisted — only the OpenAI file/batch identifiers and the user id.
    /// </summary>
    Task SubmitBatchAsync(
        Guid userId,
        string userMessage,
        string? aiResponse,
        IReadOnlyList<UserFact> existingFacts,
        CancellationToken cancellationToken = default);
}
