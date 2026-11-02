using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CompleteDurableAuthAndDeliveryState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "SentReminders" ADD COLUMN IF NOT EXISTS "PushBody" text;
            ALTER TABLE "SentReminders" ADD COLUMN IF NOT EXISTS "PushDataJson" text;
            ALTER TABLE "SentReminders" ADD COLUMN IF NOT EXISTS "PushSentAtUtc" timestamp with time zone;
            ALTER TABLE "SentReminders" ADD COLUMN IF NOT EXISTS "PushTitle" text;
            ALTER TABLE "SentReminders" ADD COLUMN IF NOT EXISTS "PushUserId" uuid;
            CREATE TABLE IF NOT EXISTS "MarketingDeliveries" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "BroadcastId" uuid NOT NULL,
                "RecipientId" uuid NOT NULL,
                "Email" character varying(320) NOT NULL,
                "Language" character varying(32) NOT NULL,
                "SubjectEn" text NOT NULL,
                "SubjectPt" text NOT NULL,
                "BodyHtmlEn" text NOT NULL,
                "BodyHtmlPt" text NOT NULL,
                "NotBeforeAtUtc" timestamp with time zone NOT NULL,
                "SentAtUtc" timestamp with time zone
            );
            CREATE TABLE IF NOT EXISTS "PendingCouponCancellations" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NOT NULL,
                "CouponId" character varying(255) NOT NULL,
                "Attempts" integer NOT NULL,
                "NextAttemptAtUtc" timestamp with time zone NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "UserCacheVersions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Version" bigint NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_SentReminders_PushSentAtUtc_SentAtUtc"
                ON "SentReminders" ("PushSentAtUtc", "SentAtUtc");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_MarketingDeliveries_BroadcastId_RecipientId"
                ON "MarketingDeliveries" ("BroadcastId", "RecipientId");
            CREATE INDEX IF NOT EXISTS "IX_MarketingDeliveries_SentAtUtc_NotBeforeAtUtc"
                ON "MarketingDeliveries" ("SentAtUtc", "NotBeforeAtUtc");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PendingCouponCancellations_CouponId"
                ON "PendingCouponCancellations" ("CouponId");
            CREATE INDEX IF NOT EXISTS "IX_PendingCouponCancellations_NextAttemptAtUtc"
                ON "PendingCouponCancellations" ("NextAttemptAtUtc");
            """);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_DistributedRateLimitBuckets_PolicyName_WindowEndsAtUtc"
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_DistributedRateLimitBuckets_PolicyName_WindowEndsAtUtc"
                ON "DistributedRateLimitBuckets" ("PolicyName", "WindowEndsAtUtc")
            """, suppressTransaction: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_DistributedRateLimitBuckets_PolicyName_WindowEndsAtUtc"
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "MarketingDeliveries";
            DROP TABLE IF EXISTS "PendingCouponCancellations";
            DROP TABLE IF EXISTS "UserCacheVersions";
            DROP INDEX IF EXISTS "IX_SentReminders_PushSentAtUtc_SentAtUtc";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushBody";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushDataJson";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushSentAtUtc";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushTitle";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushUserId";
            """);
    }
}
