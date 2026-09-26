using Microsoft.Extensions.Configuration;

namespace Orbit.Infrastructure.Configuration;

public sealed class DatabaseConnectionSettings
{
    public const string SectionName = "Database";

    public int EfMaxPoolSize { get; init; } = 15;

    public int SessionMaxPoolSize { get; init; } = 5;

    public int CommandTimeoutSeconds { get; init; } = 60;

    public int MigrationCommandTimeoutSeconds { get; init; } = 180;

    public int TransactionTimeoutSeconds { get; init; } = 120;

    public int SlowQueryThresholdMilliseconds { get; init; } = 500;

    public static DatabaseConnectionSettings From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<DatabaseConnectionSettings>()
            ?? new DatabaseConnectionSettings();
}
