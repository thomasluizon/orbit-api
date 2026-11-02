using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersistMarketingBroadcastDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketingBroadcastDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BroadcastId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketingBroadcastDeliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketingBroadcastDeliveries_BroadcastId_RecipientId",
                table: "MarketingBroadcastDeliveries",
                columns: new[] { "BroadcastId", "RecipientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketingBroadcastDeliveries_DeliveredAtUtc",
                table: "MarketingBroadcastDeliveries",
                column: "DeliveredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MarketingBroadcastDeliveries_RecipientId",
                table: "MarketingBroadcastDeliveries",
                column: "RecipientId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.DropTable(
                name: "MarketingBroadcastDeliveries");
        }
    }
}
