using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopeProcessedRequestsByOrdinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessedRequests_UserId_IdempotencyKey_RequestType",
                table: "ProcessedRequests");

            migrationBuilder.AddColumn<int>(
                name: "RequestOrdinal",
                table: "ProcessedRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedRequests_UserId_IdempotencyKey_RequestType_Request~",
                table: "ProcessedRequests",
                columns: new[] { "UserId", "IdempotencyKey", "RequestType", "RequestOrdinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessedRequests_UserId_IdempotencyKey_RequestType_Request~",
                table: "ProcessedRequests");

            migrationBuilder.DropColumn(
                name: "RequestOrdinal",
                table: "ProcessedRequests");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedRequests_UserId_IdempotencyKey_RequestType",
                table: "ProcessedRequests",
                columns: new[] { "UserId", "IdempotencyKey", "RequestType" },
                unique: true);
        }
    }
}
