using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Persistence;

internal static class JobPaging
{
    internal const int DefaultPageSize = 400;

    internal static IQueryable<T> Page<T>(this IQueryable<T> query, Guid? afterId, int pageSize = DefaultPageSize)
        where T : Entity
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 500);
        if (afterId.HasValue) query = query.Where(row => row.Id.CompareTo(afterId.Value) > 0);
        return query.OrderBy(row => row.Id).Take(pageSize);
    }
}
