using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RewriteStreakNotificationUrls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    batch_count integer;
                    total_count integer := 0;
                BEGIN
                    LOOP
                        WITH candidates AS (
                            SELECT "Id"
                            FROM "Notifications"
                            WHERE "Url" = '/streak'
                            LIMIT 1000
                        )
                        UPDATE "Notifications" AS notification
                        SET "Url" = '/progress'
                        FROM candidates
                        WHERE notification."Id" = candidates."Id";

                        GET DIAGNOSTICS batch_count = ROW_COUNT;
                        total_count := total_count + batch_count;
                        EXIT WHEN batch_count = 0;
                    END LOOP;

                    RAISE NOTICE 'Rewrote % streak notification routes', total_count;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    batch_count integer;
                BEGIN
                    LOOP
                        WITH candidates AS (
                            SELECT "Id"
                            FROM "Notifications"
                            WHERE "Url" = '/progress'
                            LIMIT 1000
                        )
                        UPDATE "Notifications" AS notification
                        SET "Url" = '/streak'
                        FROM candidates
                        WHERE notification."Id" = candidates."Id";

                        GET DIAGNOSTICS batch_count = ROW_COUNT;
                        EXIT WHEN batch_count = 0;
                    END LOOP;
                END $$;
                """);
        }
    }
}
