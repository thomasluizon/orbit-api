using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwedSubscriptionCancellations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwedSubscriptionCancellations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    ProviderIdentifier = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExhaustedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwedSubscriptionCancellations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwedSubscriptionCancellations_NextAttemptAtUtc",
                table: "OwedSubscriptionCancellations",
                column: "NextAttemptAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OwedSubscriptionCancellations_Provider_ProviderIdentifier",
                table: "OwedSubscriptionCancellations",
                columns: new[] { "Provider", "ProviderIdentifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwedSubscriptionCancellations");
        }
    }
}
