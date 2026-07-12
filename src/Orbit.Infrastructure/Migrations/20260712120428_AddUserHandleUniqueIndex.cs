using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserHandleUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT ""Id"",
                           ROW_NUMBER() OVER (
                               PARTITION BY LOWER(""Handle"")
                               ORDER BY ""CreatedAtUtc"", ""Id""
                           ) AS rn
                    FROM ""Users""
                    WHERE ""Handle"" IS NOT NULL
                )
                UPDATE ""Users"" AS u
                SET ""Handle"" = 'user_' || LEFT(REPLACE(u.""Id""::text, '-', ''), 12)
                FROM ranked
                WHERE u.""Id"" = ranked.""Id"" AND ranked.rn > 1;");

            // Idempotent: an identical IX_Users_Handle_Lower was already created by AddSocialFoundation, so a bare CREATE crashes prod migration with 42P07 (relation already exists) while tests — which never run raw-SQL migrations against real Postgres — pass. IF NOT EXISTS makes this a safe no-op on existing DBs and still correct on a fresh one. https://github.com/thomasluizon/orbit-ui-mobile/issues/243
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Users_Handle_Lower""
                    ON ""Users"" (LOWER(""Handle""))
                    WHERE ""Handle"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Users_Handle_Lower"";");
        }
    }
}
