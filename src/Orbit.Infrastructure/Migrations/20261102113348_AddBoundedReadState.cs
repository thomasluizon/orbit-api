using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBoundedReadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SELECT set_config('orbit.bounded_reads_statement_timeout', current_setting('statement_timeout'), true),
                    set_config('orbit.bounded_reads_lock_timeout', current_setting('lock_timeout'), true);
                SET LOCAL lock_timeout = '1s';
                SET LOCAL statement_timeout = '5s';
                """);

            migrationBuilder.AddColumn<DateTime>(
                name: "GoogleAccessTokenExpiresAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GoogleCalendarLastAttemptAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LongestRecordedStreak",
                table: "Habits",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "XpHistoryInitialized",
                table: "Users",
                type: "boolean",
                nullable: true);

            migrationBuilder.Sql("ALTER TABLE \"Users\" ALTER COLUMN \"XpHistoryInitialized\" SET DEFAULT TRUE;");

            migrationBuilder.CreateTable(
                name: "XpDailyTotals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    DailyXp = table.Column<int>(type: "integer", nullable: false),
                    CumulativeXp = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XpDailyTotals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_XpDailyTotals_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_XpDailyTotals_UserId_Date",
                table: "XpDailyTotals",
                columns: new[] { "UserId", "Date" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION maintain_xp_daily_totals() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE owner_id uuid; award_date date; delta integer;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        owner_id := NEW."UserId";
                        award_date := (NEW."AwardedAtUtc" AT TIME ZONE 'UTC')::date;
                        delta := NEW."Amount";
                    ELSE
                        owner_id := OLD."UserId";
                        award_date := (OLD."AwardedAtUtc" AT TIME ZONE 'UTC')::date;
                        delta := -OLD."Amount";
                    END IF;
                    PERFORM 1 FROM "Users" WHERE "Id" = owner_id FOR NO KEY UPDATE;
                    IF TG_OP = 'INSERT' THEN
                        INSERT INTO "XpDailyTotals" ("Id", "UserId", "Date", "DailyXp", "CumulativeXp")
                        VALUES (gen_random_uuid(), owner_id, award_date, delta,
                            COALESCE((SELECT "CumulativeXp" FROM "XpDailyTotals"
                                WHERE "UserId" = owner_id AND "Date" < award_date
                                ORDER BY "Date" DESC LIMIT 1), 0) + delta)
                        ON CONFLICT ("UserId", "Date") DO UPDATE SET
                            "DailyXp" = "XpDailyTotals"."DailyXp" + delta,
                            "CumulativeXp" = "XpDailyTotals"."CumulativeXp" + delta;
                    ELSE
                        UPDATE "XpDailyTotals" SET "DailyXp" = "DailyXp" + delta,
                            "CumulativeXp" = "CumulativeXp" + delta
                            WHERE "UserId" = owner_id AND "Date" = award_date;
                    END IF;
                    UPDATE "XpDailyTotals" SET "CumulativeXp" = "CumulativeXp" + delta
                        WHERE "UserId" = owner_id AND "Date" > award_date;
                    RETURN NULL;
                END $$;
                DROP TRIGGER IF EXISTS maintain_xp_daily_totals ON "XpAwardLogs";
                CREATE TRIGGER maintain_xp_daily_totals AFTER INSERT OR DELETE ON "XpAwardLogs"
                    FOR EACH ROW EXECUTE FUNCTION maintain_xp_daily_totals();
                """);

            migrationBuilder.Sql("""
                SELECT set_config('statement_timeout', current_setting('orbit.bounded_reads_statement_timeout'), true),
                    set_config('lock_timeout', current_setting('orbit.bounded_reads_lock_timeout'), true);
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '1s';
                SET LOCAL statement_timeout = '5s';
                DROP TRIGGER IF EXISTS maintain_xp_daily_totals ON "XpAwardLogs";
                DROP FUNCTION IF EXISTS maintain_xp_daily_totals();
                """);

            migrationBuilder.DropTable(
                name: "XpDailyTotals");

            migrationBuilder.DropColumn(
                name: "GoogleAccessTokenExpiresAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GoogleCalendarLastAttemptAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LongestRecordedStreak",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "XpHistoryInitialized",
                table: "Users");
        }
    }
}
