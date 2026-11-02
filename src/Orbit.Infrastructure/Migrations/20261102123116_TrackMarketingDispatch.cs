using System;
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
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.AddColumn<string>(
                name: "BackgroundJobId",
                table: "MarketingDeliveries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRecoveryAtUtc",
                table: "MarketingDeliveries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_MarketingDeliveries_SentAtUtc_NextRecoveryAtUtc",
                table: "MarketingDeliveries",
                columns: new[] { "SentAtUtc", "NextRecoveryAtUtc" })
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_MarketingDeliveries_SentAtUtc_NextRecoveryAtUtc";
                """, suppressTransaction: true);
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.DropColumn(
                name: "BackgroundJobId",
                table: "MarketingDeliveries");

            migrationBuilder.DropColumn(
                name: "NextRecoveryAtUtc",
                table: "MarketingDeliveries");
        }
    }
}
