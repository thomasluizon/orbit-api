using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

internal sealed class SqliteOrbitDbContextFactory : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrbitDbContext> _options;
    private readonly IEncryptionService? _encryption;
    private readonly string? _databasePath;

    internal SqliteOrbitDbContextFactory(params IInterceptor[] interceptors)
        : this(null, null, null, interceptors)
    {
    }

    internal SqliteOrbitDbContextFactory(
        Func<ExecutionStrategyDependencies, IExecutionStrategy>? executionStrategyFactory,
        params IInterceptor[] interceptors)
        : this(executionStrategyFactory, null, null, interceptors)
    {
    }

    internal SqliteOrbitDbContextFactory(IEncryptionService encryption, params IInterceptor[] interceptors)
        : this(null, encryption, null, interceptors)
    {
    }

    internal SqliteOrbitDbContextFactory(string databasePath, params IInterceptor[] interceptors)
        : this(null, null, databasePath, interceptors)
    {
    }

    private SqliteOrbitDbContextFactory(
        Func<ExecutionStrategyDependencies, IExecutionStrategy>? executionStrategyFactory,
        IEncryptionService? encryption,
        string? databasePath,
        IInterceptor[] interceptors)
    {
        _encryption = encryption;
        _databasePath = databasePath;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? ":memory:",
            Pooling = false
        }.ToString();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        _connection.CreateFunction<string, string>("md5", value => Convert.ToHexStringLower(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value))), isDeterministic: true);

        var options = new DbContextOptionsBuilder<OrbitDbContext>();
        void Configure(SqliteDbContextOptionsBuilder sqlite)
        {
            if (executionStrategyFactory is not null)
                sqlite.ExecutionStrategy(executionStrategyFactory);
        }

        if (databasePath is null)
            options.UseSqlite(_connection, Configure);
        else
            options.UseSqlite(connectionString, Configure);
        _options = options.AddInterceptors(interceptors)
            .ReplaceService<IModelCacheKeyFactory, EncryptionModelCacheKeyFactory>()
            .Options;

        Context = CreateContext();
        Context.Database.EnsureCreated();
    }

    internal OrbitDbContext Context { get; }

    internal OrbitDbContext CreateContext() => new SqliteCompatOrbitDbContext(_options, _encryption);

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
        if (_databasePath is not null)
            File.Delete(_databasePath);
    }

    private sealed class EncryptionModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), (context as SqliteCompatOrbitDbContext)?.Encryption, designTime);
    }

    private sealed class SqliteCompatOrbitDbContext(DbContextOptions<OrbitDbContext> options, IEncryptionService? encryption)
        : OrbitDbContext(options, encryption)
    {
        internal IEncryptionService? Encryption { get; } = encryption;

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
                    if (index.GetFilter() != "\"PushedAtUtc\" IS NOT NULL") index.SetFilter(null);
            }
        }
    }
}
