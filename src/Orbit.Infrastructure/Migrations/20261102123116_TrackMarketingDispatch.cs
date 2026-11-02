using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackMarketingDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                ALTER TABLE "MarketingDeliveries" ADD COLUMN IF NOT EXISTS "BackgroundJobId" text;
                ALTER TABLE "MarketingDeliveries" ADD COLUMN IF NOT EXISTS "NextRecoveryAtUtc"
                    timestamp with time zone NOT NULL DEFAULT TIMESTAMPTZ '-infinity';
                """);
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_MarketingDeliveries_SentAtUtc_NextRecoveryAtUtc";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_MarketingDeliveries_SentAtUtc_NextRecoveryAtUtc"
                    ON "MarketingDeliveries" ("SentAtUtc", "NextRecoveryAtUtc");
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_MarketingDeliveries_SentAtUtc_NextRecoveryAtUtc";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                ALTER TABLE "MarketingDeliveries" DROP COLUMN IF EXISTS "BackgroundJobId";
                ALTER TABLE "MarketingDeliveries" DROP COLUMN IF EXISTS "NextRecoveryAtUtc";
                """);
        }
    }
}
