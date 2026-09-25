using Microsoft.Extensions.Configuration;
using Npgsql;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Persistence;

/// <summary>
/// Resolves the API's PostgreSQL connection strings and enforces Npgsql client-pool caps in code so a
/// misconfigured deploy can never exhaust the Supabase pooler. The request path uses
/// <c>ConnectionStrings:DefaultConnection</c> (intended for the Supavisor transaction pooler); startup
/// migrations, the design-time factory, and the Hangfire durable queue use
/// <c>ConnectionStrings:SessionConnection</c> (the session pooler), falling back to
/// <c>DefaultConnection</c> when it is unset. The configured <see cref="DatabaseConnectionSettings"/>
/// pool caps override any pool size present in the raw connection string. Idle session-pool connections
/// are pruned after 60 seconds, and open inactive session connections receive keepalive queries after
/// 30 seconds to detect connections closed by the pooler or network.
/// </summary>
public static class OrbitConnectionStringFactory
{
    public static string ForRequestPath(IConfiguration configuration)
    {
        var settings = DatabaseConnectionSettings.From(configuration);
        return ApplyPoolCap(configuration.GetConnectionString("DefaultConnection"), settings.EfMaxPoolSize);
    }

    public static string ForSession(IConfiguration configuration)
    {
        var settings = DatabaseConnectionSettings.From(configuration);
        var sessionConnectionString = configuration.GetConnectionString("SessionConnection");
        if (string.IsNullOrWhiteSpace(sessionConnectionString))
            sessionConnectionString = configuration.GetConnectionString("DefaultConnection");

        var cappedConnectionString = ApplyPoolCap(sessionConnectionString, settings.SessionMaxPoolSize);
        if (string.IsNullOrEmpty(cappedConnectionString))
            return cappedConnectionString;

        return new NpgsqlConnectionStringBuilder(cappedConnectionString)
        {
            ConnectionIdleLifetime = 60,
            KeepAlive = 30
        }.ConnectionString;
    }

    private static string ApplyPoolCap(string? connectionString, int maxPoolSize)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString ?? string.Empty;

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = maxPoolSize,
            MinPoolSize = 0
        }.ConnectionString;
    }
}
