using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// Counts the SQL reader commands EF actually sends to the database so a test can assert a query's
/// round-trip count is invariant to row volume — the signature of an N+1 regression is a count that
/// grows with the seed. Only the reader hooks are counted because every read the query-shape tests
/// exercise resolves through <c>ToListAsync</c>.
/// </summary>
internal sealed class CountingDbCommandInterceptor : DbCommandInterceptor
{
    private int _commandCount;

    internal int CommandCount => Volatile.Read(ref _commandCount);

    internal void Reset() => Interlocked.Exchange(ref _commandCount, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref _commandCount);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commandCount);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
