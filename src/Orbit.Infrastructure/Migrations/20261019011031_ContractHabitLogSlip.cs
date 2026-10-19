using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContractHabitLogSlip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.Sql("""
                UPDATE "HabitLogs" AS logs
                SET "IsSlip" = logs."Value" > 0 AND
                    (SELECT "IsBadHabit" FROM "Habits" WHERE "Id" = logs."HabitId")
                WHERE logs."IsSlip" IS NULL;
                """);

            migrationBuilder.AlterColumn<bool>(
                name: "IsSlip",
                table: "HabitLogs",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "IsSlip",
                table: "HabitLogs",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
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
    }
}
