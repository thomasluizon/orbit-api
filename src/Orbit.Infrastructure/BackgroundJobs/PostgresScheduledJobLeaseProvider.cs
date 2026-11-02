using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.BackgroundJobs;

public sealed class PostgresScheduledJobLeaseProvider : IScheduledJobLeaseProvider
{
    private readonly Func<DbConnection> _createConnection;

    public PostgresScheduledJobLeaseProvider(IConfiguration configuration)
        : this(() => new NpgsqlConnection(OrbitConnectionStringFactory.ForJobLocks(configuration))) { }

    internal PostgresScheduledJobLeaseProvider(Func<DbConnection> createConnection) => _createConnection = createConnection;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string jobName, CancellationToken cancellationToken)
    {
        var connection = _createConnection();
        bool acquired;
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = CreateCommand(connection, "pg_try_advisory_lock", jobName);
            acquired = await command.ExecuteScalarAsync(cancellationToken) is true;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        if (acquired) return new Lease(connection, jobName);
        await connection.DisposeAsync();
        return null;
    }

    private static DbCommand CreateCommand(DbConnection connection, string function, string jobName)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT {function}(hashtextextended(@name, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = $"orbit-scheduled:{jobName}";
        command.Parameters.Add(parameter);
        return command;
    }

    private sealed class Lease(DbConnection connection, string jobName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = CreateCommand(connection, "pg_advisory_unlock", jobName);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
