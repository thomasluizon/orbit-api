using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributedRateLimitBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DistributedRateLimitBuckets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PartitionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowEndsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DistributedRateLimitBuckets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DistributedRateLimitBuckets_PolicyName_PartitionKey_WindowS~",
                table: "DistributedRateLimitBuckets",
                columns: new[] { "PolicyName", "PartitionKey", "WindowStartUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DistributedRateLimitBuckets");
        }
    }
}
