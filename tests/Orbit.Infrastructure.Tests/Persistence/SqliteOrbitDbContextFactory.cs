using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// Builds an <see cref="OrbitDbContext"/> over a private SQLite in-memory database — the only test
/// provider that emits real SQL, so query-shape and round-trip-count assertions have something to
/// observe. Strips the Postgres-only <c>::</c> default-value casts and filtered-index predicates the
/// SQLite DDL cannot parse (the compat shim otherwise duplicated inline across the persistence test
/// files), attaches any supplied interceptors, and keeps the backing connection open for the lifetime
/// of the in-memory database. New round-trip tests use this; the pre-existing inline copies are left
/// as a follow-up dedup (orbit-ui-mobile#461 B2).
/// </summary>
internal sealed class SqliteOrbitDbContextFactory : IDisposable
{
    private readonly SqliteConnection _connection;

    internal SqliteOrbitDbContextFactory(params IInterceptor[] interceptors)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptors)
            .Options;

        Context = new SqliteCompatOrbitDbContext(options);
        Context.Database.EnsureCreated();
    }

    internal OrbitDbContext Context { get; }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }

    private sealed class SqliteCompatOrbitDbContext(DbContextOptions<OrbitDbContext> options)
        : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    var defaultSql = property.GetDefaultValueSql();
                    if (defaultSql is not null && defaultSql.Contains("::", StringComparison.Ordinal))
                        property.SetDefaultValueSql(null);
                }

                foreach (var index in entityType.GetIndexes())
                    index.SetFilter(null);
            }
        }
    }
}
