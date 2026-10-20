using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeparateBonusProEntitlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Users"
                SET "Plan" = 0, "PlanExpiresAt" = NULL
                WHERE "BonusProExpiresAtUtc" IS NOT NULL
                  AND "PlanExpiresAt" = "BonusProExpiresAtUtc";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Users"
                SET "Plan" = 1, "PlanExpiresAt" = "BonusProExpiresAtUtc"
                WHERE "BonusProExpiresAtUtc" IS NOT NULL
                  AND ("PlanExpiresAt" IS NULL OR "PlanExpiresAt" < "BonusProExpiresAtUtc");
                """);
        }
    }
}
