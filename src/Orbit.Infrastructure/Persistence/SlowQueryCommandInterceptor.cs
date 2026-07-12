using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Persistence;

/// <summary>
/// Logs a warning for any database command whose measured execution exceeds
/// <see cref="DatabaseConnectionSettings.SlowQueryThresholdMilliseconds"/>, making slow queries observable in
/// the application logs (Render) without turning on EF's per-command Information logging in production. The
/// measured duration includes the network round trip and client-side materialization; for server-side timing
/// only, set PostgreSQL's <c>log_min_duration_statement</c> on the Supabase side as a complement:
/// https://supabase.com/docs/guides/telemetry/logs#database-logs
/// </summary>
public sealed partial class SlowQueryCommandInterceptor(
    ILogger<SlowQueryCommandInterceptor> logger,
    DatabaseConnectionSettings databaseSettings) : DbCommandInterceptor
{
    private TimeSpan Threshold => TimeSpan.FromMilliseconds(databaseSettings.SlowQueryThresholdMilliseconds);

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        LogIfSlow(command.CommandText, eventData.Duration);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command.CommandText, eventData.Duration);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        LogIfSlow(command.CommandText, eventData.Duration);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command.CommandText, eventData.Duration);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        LogIfSlow(command.CommandText, eventData.Duration);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command.CommandText, eventData.Duration);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    internal void LogIfSlow(string commandText, TimeSpan duration)
    {
        if (duration < Threshold)
            return;

        LogSlowQuery(logger, (long)duration.TotalMilliseconds, commandText);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Slow database query took {ElapsedMilliseconds}ms: {CommandText}")]
    private static partial void LogSlowQuery(ILogger logger, long elapsedMilliseconds, string commandText);
}
