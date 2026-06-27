namespace Orbit.Infrastructure.Configuration;

/// <summary>
/// Npgsql client-pool caps for the Supabase Supavisor pooler, applied in code so the API can never open
/// more backend connections than the pooler allows regardless of the deploy-time connection string.
/// <see cref="EfMaxPoolSize"/> bounds the EF Core request-path pool; <see cref="SessionMaxPoolSize"/>
/// bounds the session-pooler pool shared by startup migrations and the Hangfire durable queue. The two
/// caps together must stay below the pooler's configured pool size, leaving headroom for transient
/// connections (a rolling deploy's instance overlap, migrations, and the Supabase dashboard).
/// </summary>
public sealed class DatabaseConnectionSettings
{
    public const string SectionName = "Database";

    public int EfMaxPoolSize { get; init; } = 8;

    public int SessionMaxPoolSize { get; init; } = 2;
}
