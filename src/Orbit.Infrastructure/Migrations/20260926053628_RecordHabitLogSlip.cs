using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecordHabitLogSlip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSlip",
                table: "HabitLogs",
                type: "boolean",
                nullable: true);

            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    UPDATE "HabitLogs"
                    SET "IsSlip" = "Value" > 0 AND
                        (SELECT "IsBadHabit" FROM "Habits" WHERE "Id" = "HabitLogs"."HabitId");
                    """);

                migrationBuilder.Sql("""
                    CREATE TRIGGER "SetHabitLogIsSlipOnInsert"
                    AFTER INSERT ON "HabitLogs"
                    FOR EACH ROW WHEN NEW."IsSlip" IS NULL
                    BEGIN
                        UPDATE "HabitLogs"
                        SET "IsSlip" = NEW."Value" > 0 AND
                            (SELECT "IsBadHabit" FROM "Habits" WHERE "Id" = NEW."HabitId")
                        WHERE "Id" = NEW."Id";
                    END;
                    """);
            }
            else
            {
                migrationBuilder.Sql("""
                    UPDATE "HabitLogs" AS logs
                    SET "IsSlip" = logs."Value" > 0 AND habits."IsBadHabit"
                    FROM "Habits" AS habits
                    WHERE logs."HabitId" = habits."Id";
                    """);

                migrationBuilder.Sql("""
                    CREATE FUNCTION "SetHabitLogIsSlipOnInsert"() RETURNS trigger AS $trigger$
                    BEGIN
                        IF NEW."IsSlip" IS NULL THEN
                            SELECT NEW."Value" > 0 AND habits."IsBadHabit"
                            INTO NEW."IsSlip"
                            FROM "Habits" AS habits
                            WHERE habits."Id" = NEW."HabitId";
                        END IF;
                        RETURN NEW;
                    END;
                    $trigger$ LANGUAGE plpgsql;

                    CREATE TRIGGER "SetHabitLogIsSlipOnInsert"
                    BEFORE INSERT ON "HabitLogs"
                    FOR EACH ROW WHEN (NEW."IsSlip" IS NULL)
                    EXECUTE FUNCTION "SetHabitLogIsSlipOnInsert"();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    DROP TRIGGER IF EXISTS "SetHabitLogIsSlipOnInsert";
                    """);
            }
            else
            {
                migrationBuilder.Sql("""
                    DROP TRIGGER IF EXISTS "SetHabitLogIsSlipOnInsert" ON "HabitLogs";
                    DROP FUNCTION IF EXISTS "SetHabitLogIsSlipOnInsert"();
                    """);
            }

            migrationBuilder.DropColumn(
                name: "IsSlip",
                table: "HabitLogs");
        }
    }
}
