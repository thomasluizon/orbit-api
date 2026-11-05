using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

/// <summary>Identifies entities already tracked before a locked mutation reads its current state.</summary>
public interface ITrackedEntityLookup<T> where T : Entity
{
    bool HasTrackedEntities(IReadOnlyCollection<Guid> ids);
}
