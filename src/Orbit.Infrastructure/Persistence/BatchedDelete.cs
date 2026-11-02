using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Persistence;

internal static class BatchedDelete
{
    internal const int DefaultBatchSize = 5000;

    public static async Task<int> ExecuteAsync<T>(IQueryable<T> query, CancellationToken cancellationToken,
        int batchSize = DefaultBatchSize) where T : Entity
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var total = 0;
        while (true)
        {
            var deleted = await query.OrderBy(row => row.Id).Take(batchSize).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
            if (deleted < batchSize) return total;
        }
    }
}
