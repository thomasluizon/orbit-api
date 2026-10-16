using Microsoft.EntityFrameworkCore;

namespace Orbit.Infrastructure.Persistence;

internal static class UserRowLock
{
    public static Task AcquireAsync(
        OrbitDbContext context,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return Task.CompletedTask;

        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A user row lock requires an active transaction.");

        return context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {userId} FOR NO KEY UPDATE", cancellationToken);
    }
}
