using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class DatabaseHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenDatabaseReachable_ReturnsHealthy()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new OrbitDbContext(options);
        var healthCheck = new DatabaseHealthCheck(context);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDatabaseUnreachable_ReturnsUnhealthy()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=orbit;Username=none;Password=none;Timeout=1;Command Timeout=1")
            .Options;

        using var context = new OrbitDbContext(options);
        var healthCheck = new DatabaseHealthCheck(context);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
