using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopeProcessedRequestsByContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessedRequests_UserId_IdempotencyKey_RequestType",
                table: "ProcessedRequests");

            migrationBuilder.AddColumn<string>(
                name: "RequestFingerprint",
                table: "ProcessedRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedRequests_UserId_IdempotencyKey_RequestType_Request~",
                table: "ProcessedRequests",
                columns: new[] { "UserId", "IdempotencyKey", "RequestType", "RequestFingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessedRequests_UserId_IdempotencyKey_RequestType_Request~",
                table: "ProcessedRequests");

            migrationBuilder.DropColumn(
                name: "RequestFingerprint",
                table: "ProcessedRequests");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedRequests_UserId_IdempotencyKey_RequestType",
                table: "ProcessedRequests",
                columns: new[] { "UserId", "IdempotencyKey", "RequestType" },
                unique: true);
        }
    }
}
