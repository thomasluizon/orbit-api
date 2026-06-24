using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

/// <summary>
/// Tracks one OpenAI Batch API job that extracts personal facts from a single Pro chat turn.
/// Only identifiers are stored — never the conversation text or extracted facts — because the
/// prompt and results live exclusively in the OpenAI-hosted input/output files referenced here.
/// </summary>
public class AiFactExtractionBatch : Entity
{
    public Guid UserId { get; private set; }
    public string BatchId { get; private set; } = null!;
    public string InputFileId { get; private set; } = null!;
    public string? OutputFileId { get; private set; }
    public AiFactExtractionBatchStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private AiFactExtractionBatch() { }

    public static AiFactExtractionBatch Create(Guid userId, string batchId, string inputFileId)
    {
        return new AiFactExtractionBatch
        {
            UserId = userId,
            BatchId = batchId,
            InputFileId = inputFileId,
            Status = AiFactExtractionBatchStatus.Submitted,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public void MarkCompleted(string outputFileId)
    {
        OutputFileId = outputFileId;
        Status = AiFactExtractionBatchStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
    }

    public void MarkFailed()
    {
        Status = AiFactExtractionBatchStatus.Failed;
        CompletedAtUtc = DateTime.UtcNow;
    }
}
