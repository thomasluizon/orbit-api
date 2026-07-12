using Microsoft.Extensions.Configuration;

namespace Orbit.Infrastructure.Configuration;

/// <summary>
/// Npgsql client-pool caps and command timeouts, applied in code so the API can never open more backend
/// connections than the Supabase Supavisor pooler allows, regardless of the deploy-time connection string.
/// <para>
/// Sizing model (Supabase compute has <c>max_connections = 60</c>, 3 reserved, so ~57 usable direct backends):
/// the request path (<see cref="EfMaxPoolSize"/>) runs through the Supavisor <b>transaction</b> pooler, which
/// multiplexes — a client connection only holds a Postgres backend for the duration of a transaction — so this
/// cap is a request-concurrency limit that is decoupled from the held backend count and can safely exceed the
/// pooler's server-side pool size. The session path (<see cref="SessionMaxPoolSize"/>) runs through the
/// <b>session</b> pooler, where each client connection holds a dedicated Postgres backend for its whole lifetime
/// (1:1); it is shared by startup migrations and the always-on Hangfire durable queue (2 workers + the storage
/// expiration/heartbeat threads + request-path enqueue), so it must stay well under the usable-backend ceiling.
/// At 15 + 5 the worst case — a rolling deploy's brief two-instance overlap (2 × 5 = 10 held session backends)
/// plus Supabase's own internal connections — stays comfortably under 57.
/// </para>
/// <para>
/// Timeouts: <see cref="CommandTimeoutSeconds"/> bounds request-path queries (raised from 30s so a long
/// transaction is not clipped; the long AI/batch work is OpenAI network I/O bounded separately by
/// <c>AI:BatchNetworkTimeoutSeconds</c>, not a single long DB command). <see cref="MigrationCommandTimeoutSeconds"/>
/// is deliberately larger because a startup migration can build an index or backfill a table in one statement
/// that far exceeds the request-path budget. See thomasluizon/orbit-ui-mobile#243.
/// </para>
/// </summary>
public sealed class DatabaseConnectionSettings
{
    public const string SectionName = "Database";

    public int EfMaxPoolSize { get; init; } = 15;

    public int SessionMaxPoolSize { get; init; } = 5;

    public int CommandTimeoutSeconds { get; init; } = 60;

    public int MigrationCommandTimeoutSeconds { get; init; } = 180;

    public static DatabaseConnectionSettings From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<DatabaseConnectionSettings>()
            ?? new DatabaseConnectionSettings();
}
