using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

internal sealed class SqliteOrbitDbContextFactory : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrbitDbContext> _options;

    internal SqliteOrbitDbContextFactory(params IInterceptor[] interceptors)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptors)
            .Options;

        Context = CreateContext();
        Context.Database.EnsureCreated();
    }

    internal OrbitDbContext Context { get; }

    internal OrbitDbContext CreateContext() => new SqliteCompatOrbitDbContext(_options);

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
