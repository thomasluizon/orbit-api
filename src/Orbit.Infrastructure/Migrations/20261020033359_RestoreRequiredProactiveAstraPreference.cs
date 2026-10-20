using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestoreRequiredProactiveAstraPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    CREATE FUNCTION "NormalizeUsersProactiveAstraEnabled"() RETURNS trigger AS $normalize$
                    BEGIN
                        NEW."ProactiveAstraEnabled" := COALESCE(NEW."ProactiveAstraEnabled", TRUE);
                        RETURN NEW;
                    END;
                    $normalize$ LANGUAGE plpgsql;

                    CREATE TRIGGER "NormalizeProactiveAstraEnabledOnWrite"
                    BEFORE INSERT OR UPDATE ON "Users"
                    FOR EACH ROW EXECUTE FUNCTION "NormalizeUsersProactiveAstraEnabled"();
                    """);
            }

            migrationBuilder.Sql("""
                UPDATE "Users" SET "ProactiveAstraEnabled" = TRUE
                WHERE "ProactiveAstraEnabled" IS NULL;
                """);

            migrationBuilder.AlterColumn<bool>(
                name: "ProactiveAstraEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "ProactiveAstraEnabled",
                table: "Users",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);

            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    DROP TRIGGER IF EXISTS "NormalizeProactiveAstraEnabledOnWrite" ON "Users";
                    DROP FUNCTION IF EXISTS "NormalizeUsersProactiveAstraEnabled"();
                    """);
            }
        }
    }
}
