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
    private int _selectCommandCount;
    private readonly System.Collections.Concurrent.ConcurrentQueue<CapturedDbCommand> _commands = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DbCommand, CapturedDbCommand> _captured = new();

    internal int CommandCount => Volatile.Read(ref _commandCount);
    internal int SelectCommandCount => Volatile.Read(ref _selectCommandCount);
    internal IReadOnlyList<CapturedDbCommand> Commands => _commands.ToArray();

    internal void Reset()
    {
        Interlocked.Exchange(ref _commandCount, 0);
        Interlocked.Exchange(ref _selectCommandCount, 0);
        _commands.Clear();
        _captured.Clear();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref _commandCount);
        CountSelect(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commandCount);
        CountSelect(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result) =>
        new RowCountingDbDataReader(result, _captured[command]);

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<DbDataReader>(new RowCountingDbDataReader(result, _captured[command]));

    private void CountSelect(DbCommand command)
    {
        var captured = new CapturedDbCommand(
            command.CommandText,
            command.Parameters.Cast<DbParameter>()
                .Select(parameter => parameter.Value?.ToString() ?? string.Empty)
                .ToArray());
        _commands.Enqueue(captured);
        _captured[command] = captured;
        if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref _selectCommandCount);
    }
}

internal sealed class CapturedDbCommand(string sql, IReadOnlyList<string> parameters)
{
    private int _rows;
    internal string Sql { get; } = sql;
    internal IReadOnlyList<string> Parameters { get; } = parameters;
    internal int Rows => Volatile.Read(ref _rows);
    internal void CountRow() => Interlocked.Increment(ref _rows);
}
