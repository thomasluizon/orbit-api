using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenAstraTurnReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModelContextJson",
                table: "ConversationTurns",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ConversationTurns_ConversationId_UserId"
                ON "ConversationTurns" ("ConversationId", "UserId");
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_ConversationTurns_ConversationId_UserId";
                """, suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "ModelContextJson",
                table: "ConversationTurns");
        }
    }
}
