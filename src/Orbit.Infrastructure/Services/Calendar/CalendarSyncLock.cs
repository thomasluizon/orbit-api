using Microsoft.EntityFrameworkCore;
using Orbit.Application.Calendar.Services;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services.Calendar;

internal sealed class CalendarSyncLock(OrbitDbContext context) : ICalendarSyncLock
{
    public async Task<bool> TryAcquireAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return true;

        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Calendar sync requires an active transaction.");

        var key = $"calendar-sync:{userId}";
        var acquired = await context.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtext({key})) AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (!acquired)
            return false;

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE", cancellationToken);
        return true;
    }
}
