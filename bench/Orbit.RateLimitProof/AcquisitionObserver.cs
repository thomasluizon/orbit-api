using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Orbit.RateLimitProof;

public sealed class AcquisitionObserver : DbTransactionInterceptor, ISaveChangesInterceptor
{
    public int Starts { get; private set; }
    public int Saves { get; private set; }

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
        TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        Starts++;
        return ValueTask.FromResult(result);
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Saves++;
        return ValueTask.FromResult(result);
    }
}

