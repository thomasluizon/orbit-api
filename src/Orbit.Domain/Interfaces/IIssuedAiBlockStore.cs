using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface IIssuedAiBlockStore
{
    Task AddAsync(IssuedAiBlock block, CancellationToken cancellationToken);
    Task<IssuedAiBlock?> GetAsync(Guid blockId, Guid userId, CancellationToken cancellationToken);
    Task<bool> MarkAnsweredAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, CancellationToken cancellationToken);
    Task RecordAnswerAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, string answerJson,
        DateOnly? reservedAllowanceDate, string? responseJson, CancellationToken cancellationToken);
    Task<bool> CompleteAnswerAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, string responseJson,
        CancellationToken cancellationToken);
    Task<bool> CheckpointAnswerAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, string responseJson,
        CancellationToken cancellationToken);
    Task<bool> ReleaseAnswerAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, CancellationToken cancellationToken);
    Task InvalidateOpenAsync(Guid userId, CancellationToken cancellationToken);
}
