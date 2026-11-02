using Microsoft.Extensions.Configuration;
using Npgsql;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Persistence;

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
            KeepAlive = 30
        }.ConnectionString;
    }

    public static string ForHangfire(IConfiguration configuration)
    {
        var source = configuration.GetConnectionString("HangfireConnection");
        if (string.IsNullOrWhiteSpace(source)) source = ForSession(configuration);
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        return new NpgsqlConnectionStringBuilder(source)
        {
            ApplicationName = "orbit-hangfire",
            MaxPoolSize = 12,
            MinPoolSize = 1,
            KeepAlive = 30
        }.ConnectionString;
    }

    public static string ForJobLocks(IConfiguration configuration)
    {
        var source = ForSession(configuration);
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        return new NpgsqlConnectionStringBuilder(source)
        {
            ApplicationName = "orbit-job-locks",
            MaxPoolSize = 8,
            MinPoolSize = 0
        }.ConnectionString;
    }

    private static string ApplyPoolCap(string? connectionString, int maxPoolSize)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString ?? string.Empty;

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = maxPoolSize,
            MinPoolSize = 1,
            ConnectionIdleLifetime = 300
        }.ConnectionString;
    }
}
