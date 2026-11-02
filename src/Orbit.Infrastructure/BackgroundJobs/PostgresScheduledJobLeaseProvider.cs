using Microsoft.Extensions.Configuration;
using Npgsql;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.BackgroundJobs;

public sealed class PostgresScheduledJobLeaseProvider(IConfiguration configuration) : IScheduledJobLeaseProvider
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(string jobName, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(OrbitConnectionStringFactory.ForJobLocks(configuration));
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@name, 0))", connection);
            command.Parameters.AddWithValue("name", $"orbit-scheduled:{jobName}");
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
                return new Lease(connection, jobName);
            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(NpgsqlConnection connection, string jobName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@name, 0))", connection);
                command.Parameters.AddWithValue("name", $"orbit-scheduled:{jobName}");
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
