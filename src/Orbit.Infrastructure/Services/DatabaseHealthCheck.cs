using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Reports the <c>/health</c> endpoint as unhealthy when the API cannot reach PostgreSQL, so a load balancer
/// or uptime monitor observes a database outage instead of a superficially-live process. Uses EF Core's
/// <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.CanConnectAsync"/>, which opens a
/// pooled connection and runs a trivial probe query — a full round trip through the Supavisor pooler.
/// </summary>
public sealed class DatabaseHealthCheck(OrbitDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);

        return canConnect
            ? HealthCheckResult.Healthy("Database reachable")
            : HealthCheckResult.Unhealthy("Database unreachable");
    }
}
