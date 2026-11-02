using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersistOAuthClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                ALTER TABLE "OAuthStates" ADD COLUMN IF NOT EXISTS "PartitionKey" character varying(64);
                CREATE TABLE IF NOT EXISTS "OAuthClients" (
                    "Id" uuid NOT NULL,
                    "ClientName" character varying(200) NOT NULL,
                    "RedirectUris" text[] NOT NULL,
                    "AuthorizedAtUtc" timestamp with time zone,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_OAuthClients" PRIMARY KEY ("Id")
                );
                """);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_OAuthStates_Kind_PartitionKey_ExpiresAtUtc";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_OAuthStates_Kind_PartitionKey_ExpiresAtUtc"
                ON "OAuthStates" ("Kind", "PartitionKey", "ExpiresAtUtc");
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"OAuthClients\";");

            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_OAuthStates_Kind_PartitionKey_ExpiresAtUtc\";", suppressTransaction: true);
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                ALTER TABLE "OAuthStates" DROP COLUMN IF EXISTS "PartitionKey";
                """);
        }
    }
}
