using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Proactive.Services;

public interface IProactiveEngine
{
    Task EvaluateAsync(CancellationToken cancellationToken);
    Task<ProactiveItem?> GetCurrentAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool?> IsItemActiveAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);
    Task<Result> RecordOutcomeAsync(Guid userId, Guid itemId, bool acted, CancellationToken cancellationToken);
}
