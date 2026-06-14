using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Orbit.Application.Common;

/// <summary>
/// Detects PostgreSQL unique-constraint violations (SQLSTATE 23505) surfaced through EF Core,
/// so concurrent inserts that lose the uniqueness race can be resolved idempotently instead of
/// bubbling up as an unhandled 500.
/// </summary>
public static class DbUniqueViolation
{
    private const string PostgresUniqueViolationSqlState = "23505";

    public static bool IsUniqueViolation(Exception exception)
    {
        return exception switch
        {
            DbUpdateException dbUpdateException => dbUpdateException.InnerException is not null
                && IsUniqueViolation(dbUpdateException.InnerException),
            DbException dbException => dbException.SqlState == PostgresUniqueViolationSqlState,
            _ => false
        };
    }
}
