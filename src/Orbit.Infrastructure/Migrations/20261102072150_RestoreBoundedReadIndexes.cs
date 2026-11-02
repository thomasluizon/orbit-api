using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestoreBoundedReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_AiUsageDaily_UserId\";", suppressTransaction: true);
            migrationBuilder.CreateIndex(
                name: "IX_AiUsageDaily_UserId",
                table: "AiUsageDaily",
                column: "UserId")
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_AiUsageDaily_UserId\";", suppressTransaction: true);
        }
    }
}
