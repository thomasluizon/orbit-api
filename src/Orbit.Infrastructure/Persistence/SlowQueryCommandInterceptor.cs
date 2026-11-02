using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Persistence;

public sealed partial class SlowQueryCommandInterceptor(
    ILogger<SlowQueryCommandInterceptor> logger,
    DatabaseConnectionSettings databaseSettings) : DbCommandInterceptor
{
    private TimeSpan Threshold => TimeSpan.FromMilliseconds(databaseSettings.SlowQueryThresholdMilliseconds);

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        LogIfSlow(eventData.Duration);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(eventData.Duration);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        LogIfSlow(eventData.Duration);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(eventData.Duration);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        LogIfSlow(eventData.Duration);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(eventData.Duration);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    internal void LogIfSlow(TimeSpan duration)
    {
        if (duration < Threshold)
            return;

        if (Current.Value is { } request)
            request.Record(logger, (long)duration.TotalMilliseconds);
        else
            LogSlowQueries(logger, 1, (long)duration.TotalMilliseconds, (long)duration.TotalMilliseconds);
    }

    private static readonly AsyncLocal<RequestSummary?> Current = new();

    public static IDisposable BeginRequest() => new RequestSummary();

    private sealed class RequestSummary : IDisposable
    {
        private readonly RequestSummary? _previous = Current.Value;
        private readonly object _gate = new();
        private ILogger? _logger;
        private int _count;
        private long _total;
        private long _max;

        public RequestSummary() => Current.Value = this;

        public void Record(ILogger logger, long milliseconds)
        {
            lock (_gate)
            {
                _logger = logger;
                _count++;
                _total += milliseconds;
                _max = Math.Max(_max, milliseconds);
            }
        }

        public void Dispose()
        {
            Current.Value = _previous;
            lock (_gate)
                if (_logger is not null) LogSlowQueries(_logger, _count, _total, _max);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Request ran {SlowQueryCount} slow database commands; total {TotalMilliseconds}ms, maximum {MaxMilliseconds}ms")]
    private static partial void LogSlowQueries(ILogger logger, int slowQueryCount, long totalMilliseconds, long maxMilliseconds);
}
