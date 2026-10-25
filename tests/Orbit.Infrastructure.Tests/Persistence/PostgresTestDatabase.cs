using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Orbit.Api.Extensions;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

internal static class PostgresTestDatabase
{
    internal const string ConnectionVariable = "ORBIT_POSTGRES_TEST_CONNECTION";

    internal static async Task WithDatabase(Func<OrbitDbContext, Task> operation, string? targetMigration = null)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable))
        {
            Database = $"orbit_oauth_test_{Guid.NewGuid():N}",
            Pooling = false
        };
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connection.ConnectionString
        });
        builder.Services.AddHttpContextAccessor();
        builder.AddOrbitDatabase();
        await using var provider = builder.Services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var strategy = db.Database.CreateExecutionStrategy().Should().BeOfType<NpgsqlRetryingExecutionStrategy>().Subject;
        strategy.MaxRetryCount.Should().Be(3);
        strategy.MaxRetryDelay.Should().Be(TimeSpan.FromSeconds(5));

        try
        {
            await db.GetService<IMigrator>().MigrateAsync(targetMigration);
            await operation(db);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}

internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresTestDatabase.ConnectionVariable)))
            Skip = $"Set {PostgresTestDatabase.ConnectionVariable} to a disposable PostgreSQL server with database creation permission.";
    }
}
