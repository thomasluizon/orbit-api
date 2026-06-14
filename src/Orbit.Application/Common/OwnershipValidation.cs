using Orbit.Domain.Common;

namespace Orbit.Application.Common;

/// <summary>
/// Guards that every referenced id a caller supplied resolved to an entity the user actually owns.
/// Link/create/update paths fetch related entities with an ownership-scoped query; this surfaces a
/// clear error when an id was missing or foreign instead of silently dropping it.
/// </summary>
public static class OwnershipValidation
{
    public static Result AllResolved<T>(
        IReadOnlyList<Guid> requestedIds,
        IReadOnlyCollection<T> resolved,
        Func<T, Guid> idSelector,
        AppError missingError)
    {
        var resolvedIds = resolved.Select(idSelector).ToHashSet();
        var allPresent = requestedIds.All(resolvedIds.Contains);
        return allPresent ? Result.Success() : Result.Failure(missingError);
    }
}
