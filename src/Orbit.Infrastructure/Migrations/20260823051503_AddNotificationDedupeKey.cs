using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDedupeKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DedupeKey",
                table: "Notifications",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    batch_count integer;
                    total_count integer := 0;
                BEGIN
                    LOOP
                        WITH ranked AS (
                            SELECT "Id",
                                   "Url",
                                   ROW_NUMBER() OVER (
                                       PARTITION BY "Url"
                                       ORDER BY "CreatedAtUtc", "Id") AS duplicate_rank
                            FROM "Notifications"
                            WHERE "DedupeKey" IS NULL
                              AND "Url" NOT LIKE '/%'
                              AND "Url" ~ '^goal-deadline-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-(7|3|1)d$'
                        ),
                        candidates AS (
                            SELECT "Id", "Url", duplicate_rank
                            FROM ranked
                            ORDER BY "Url", duplicate_rank
                            LIMIT 1000
                        )
                        UPDATE "Notifications" AS notification
                        SET "DedupeKey" = CASE
                                WHEN candidates.duplicate_rank = 1
                                  AND NOT EXISTS (
                                      SELECT 1
                                      FROM "Notifications" AS existing
                                      WHERE existing."DedupeKey" = candidates."Url")
                                THEN candidates."Url"
                                ELSE candidates."Url" || '-duplicate-' || candidates."Id"::text
                            END,
                            "Url" = '/progress'
                        FROM candidates
                        WHERE notification."Id" = candidates."Id";

                        GET DIAGNOSTICS batch_count = ROW_COUNT;
                        total_count := total_count + batch_count;
                        EXIT WHEN batch_count = 0;
                    END LOOP;

                    RAISE NOTICE 'Backfilled % goal deadline notification routes', total_count;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DedupeKey",
                table: "Notifications",
                column: "DedupeKey",
                unique: true,
                filter: "\"DedupeKey\" IS NOT NULL");
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
                            WHERE "DedupeKey" ~ '^goal-deadline-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-(7|3|1)d(-duplicate-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})?$'
                            LIMIT 1000
                        )
                        UPDATE "Notifications" AS notification
                        SET "Url" = regexp_replace(
                                notification."DedupeKey",
                                '-duplicate-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$',
                                ''),
                            "DedupeKey" = NULL
                        FROM candidates
                        WHERE notification."Id" = candidates."Id";

                        GET DIAGNOSTICS batch_count = ROW_COUNT;
                        EXIT WHEN batch_count = 0;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Notifications_DedupeKey",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DedupeKey",
                table: "Notifications");
        }
    }
}
