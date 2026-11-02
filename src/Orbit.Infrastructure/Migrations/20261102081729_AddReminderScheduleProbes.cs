using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderScheduleProbes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReminderScheduleProbes",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderScheduleProbes", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_ReminderScheduleProbes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.Sql("""
                CREATE FUNCTION orbit_bump_reminder_schedule_probe() RETURNS trigger AS $$
                DECLARE owner_id uuid;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NOT NEW."ReminderEnabled" THEN RETURN NULL; END IF;
                        owner_id := NEW."UserId";
                    ELSIF TG_OP = 'UPDATE' THEN
                        IF NOT (OLD."ReminderEnabled" OR NEW."ReminderEnabled") THEN RETURN NULL; END IF;
                        owner_id := NEW."UserId";
                    ELSE
                        IF NOT OLD."ReminderEnabled" THEN RETURN NULL; END IF;
                        owner_id := OLD."UserId";
                    END IF;
                    INSERT INTO "ReminderScheduleProbes" ("UserId", "Version")
                    SELECT owner_id, 1 WHERE EXISTS (SELECT 1 FROM "Users" WHERE "Id" = owner_id)
                    ON CONFLICT ("UserId") DO UPDATE SET "Version" = "ReminderScheduleProbes"."Version" + 1;
                    IF TG_OP = 'UPDATE' THEN
                        IF OLD."UserId" IS DISTINCT FROM NEW."UserId" THEN
                            INSERT INTO "ReminderScheduleProbes" ("UserId", "Version")
                            SELECT OLD."UserId", 1 WHERE EXISTS (SELECT 1 FROM "Users" WHERE "Id" = OLD."UserId")
                            ON CONFLICT ("UserId") DO UPDATE SET "Version" = "ReminderScheduleProbes"."Version" + 1;
                        END IF;
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER "TR_Habits_ReminderScheduleProbe" AFTER INSERT OR UPDATE OR DELETE ON "Habits"
                FOR EACH ROW EXECUTE FUNCTION orbit_bump_reminder_schedule_probe();
                INSERT INTO "ReminderScheduleProbes" ("UserId", "Version")
                SELECT DISTINCT "UserId", 1 FROM "Habits" WHERE "ReminderEnabled" = TRUE AND "IsCompleted" = FALSE
                ON CONFLICT ("UserId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Habits_ReminderScheduleProbe" ON "Habits";
                DROP FUNCTION IF EXISTS orbit_bump_reminder_schedule_probe();
                """);
            migrationBuilder.DropTable(
                name: "ReminderScheduleProbes");
        }
    }
}
