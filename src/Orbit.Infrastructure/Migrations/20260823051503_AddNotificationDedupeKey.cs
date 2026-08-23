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
                        WITH candidates AS (
                            SELECT "Id"
                            FROM "Notifications"
                            WHERE "DedupeKey" IS NULL
                              AND "Url" NOT LIKE '/%'
                              AND "Url" ~ '^goal-deadline-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-(7|3|1)d$'
                            LIMIT 1000
                        )
                        UPDATE "Notifications" AS notification
                        SET "DedupeKey" = notification."Url",
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
                            WHERE "DedupeKey" ~ '^goal-deadline-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-(7|3|1)d$'
                            LIMIT 1000
                        )
                        UPDATE "Notifications" AS notification
                        SET "Url" = notification."DedupeKey",
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
