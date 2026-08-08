using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecordAstraChatTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiUsageDaily_Date_Model_Purpose",
                table: "AiUsageDaily");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "AiUsageDaily",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiUsageDaily_Date_Model_Purpose_UserId"
                ON "AiUsageDaily" ("Date", "Model", "Purpose", "UserId") NULLS NOT DISTINCT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiUsageDaily_Date_Model_Purpose_UserId",
                table: "AiUsageDaily");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "AiUsageDaily");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageDaily_Date_Model_Purpose",
                table: "AiUsageDaily",
                columns: new[] { "Date", "Model", "Purpose" },
                unique: true);
        }
    }
}
