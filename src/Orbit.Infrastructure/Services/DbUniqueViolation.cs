using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Orbit.Infrastructure.Services;

internal static class DbUniqueViolation
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
