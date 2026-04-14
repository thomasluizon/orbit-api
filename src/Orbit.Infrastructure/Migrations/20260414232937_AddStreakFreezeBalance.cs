using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStreakFreezeBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""LastFreezeEarnedAtStreak"" integer NOT NULL DEFAULT 0;");
            migrationBuilder.Sql(@"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""StreakFreezeBalance"" integer NOT NULL DEFAULT 0;");

            migrationBuilder.Sql(@"UPDATE ""Users"" SET ""LastFreezeEarnedAtStreak"" = ""CurrentStreak"" WHERE ""LastFreezeEarnedAtStreak"" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Users"" DROP COLUMN IF EXISTS ""LastFreezeEarnedAtStreak"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Users"" DROP COLUMN IF EXISTS ""StreakFreezeBalance"";");
        }
    }
}
