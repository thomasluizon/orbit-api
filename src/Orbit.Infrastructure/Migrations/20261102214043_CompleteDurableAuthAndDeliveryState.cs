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
            SET LOCAL lock_timeout = '5s';
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
                "SentAtUtc" timestamp with time zone,
                "BackgroundJobId" text,
                "NextRecoveryAtUtc" timestamp with time zone NOT NULL DEFAULT TIMESTAMPTZ '-infinity'
            );
            CREATE TABLE IF NOT EXISTS "PendingCouponCancellations" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NOT NULL,
                "CouponId" character varying(255) NOT NULL,
                "Attempts" integer NOT NULL,
                "NextAttemptAtUtc" timestamp with time zone NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "EmailChallenges" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Operation" character varying(64) NOT NULL,
                "Subject" character varying(320) NOT NULL,
                "CodeHash" bytea NOT NULL,
                "Attempts" integer NOT NULL,
                "AttemptWindowEndsAtUtc" timestamp with time zone NOT NULL,
                "IssuedAtUtc" timestamp with time zone NOT NULL,
                "ExpiresAtUtc" timestamp with time zone NOT NULL,
                "Consumed" boolean NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "UserCacheVersions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Version" bigint NOT NULL
            );
            """);
        migrationBuilder.Sql("SET lock_timeout = '5s';", suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_SentReminders_PushSentAtUtc_SentAtUtc";
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_SentReminders_PushSentAtUtc_SentAtUtc"
                ON "SentReminders" ("PushSentAtUtc", "SentAtUtc");
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_MarketingDeliveries_BroadcastId_RecipientId";
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS "IX_MarketingDeliveries_BroadcastId_RecipientId"
                ON "MarketingDeliveries" ("BroadcastId", "RecipientId");
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_MarketingDeliveries_SentAtUtc_NotBeforeAtUtc";
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_MarketingDeliveries_SentAtUtc_NotBeforeAtUtc"
                ON "MarketingDeliveries" ("SentAtUtc", "NotBeforeAtUtc");
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_PendingCouponCancellations_CouponId";
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS "IX_PendingCouponCancellations_CouponId"
                ON "PendingCouponCancellations" ("CouponId");
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_PendingCouponCancellations_NextAttemptAtUtc";
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_PendingCouponCancellations_NextAttemptAtUtc"
                ON "PendingCouponCancellations" ("NextAttemptAtUtc");
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_DistributedRateLimitBuckets_PolicyName_WindowEndsAtUtc";
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_DistributedRateLimitBuckets_PolicyName_WindowEndsAtUtc"
                ON "DistributedRateLimitBuckets" ("PolicyName", "WindowEndsAtUtc");
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_MarketingDeliveries_SentAtUtc_NextRecoveryAtUtc";
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_MarketingDeliveries_SentAtUtc_NextRecoveryAtUtc"
                ON "MarketingDeliveries" ("SentAtUtc", "NextRecoveryAtUtc");
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_EmailChallenges_ExpiresAtUtc";
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_EmailChallenges_ExpiresAtUtc"
                ON "EmailChallenges" ("ExpiresAtUtc");
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_EmailChallenges_Operation_Subject";
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS "IX_EmailChallenges_Operation_Subject"
                ON "EmailChallenges" ("Operation", "Subject");
            """, suppressTransaction: true);
        migrationBuilder.Sql("RESET lock_timeout;", suppressTransaction: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SET lock_timeout = '5s';", suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_DistributedRateLimitBuckets_PolicyName_WindowEndsAtUtc"
            """, suppressTransaction: true);
        migrationBuilder.Sql("""
            DROP INDEX CONCURRENTLY IF EXISTS "IX_SentReminders_PushSentAtUtc_SentAtUtc";
            """, suppressTransaction: true);
        migrationBuilder.Sql("RESET lock_timeout;", suppressTransaction: true);
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            DROP TABLE IF EXISTS "EmailChallenges";
            DROP TABLE IF EXISTS "MarketingDeliveries";
            DROP TABLE IF EXISTS "PendingCouponCancellations";
            DROP TABLE IF EXISTS "UserCacheVersions";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushBody";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushDataJson";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushSentAtUtc";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushTitle";
            ALTER TABLE "SentReminders" DROP COLUMN IF EXISTS "PushUserId";
            """);
    }
}
