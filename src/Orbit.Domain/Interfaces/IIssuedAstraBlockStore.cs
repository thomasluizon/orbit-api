using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface IIssuedAstraBlockStore
{
    Task AddAsync(IssuedAstraBlock block, CancellationToken cancellationToken);
    Task<IssuedAstraBlock?> GetAsync(Guid blockId, Guid userId, CancellationToken cancellationToken);
    Task<bool> MarkAnsweredAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, CancellationToken cancellationToken);
    Task InvalidateOpenAsync(Guid userId, CancellationToken cancellationToken);
}
