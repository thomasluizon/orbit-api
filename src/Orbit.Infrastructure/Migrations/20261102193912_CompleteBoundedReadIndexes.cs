using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteBoundedReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_XpAwardLogs_UserId_Source_SourceId";
                """, suppressTransaction: true);
            migrationBuilder.CreateIndex(
                name: "IX_XpAwardLogs_UserId_Source_SourceId",
                table: "XpAwardLogs",
                columns: new[] { "UserId", "Source", "SourceId" })
                .Annotation("Npgsql:CreatedConcurrently", true);
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_Users_GoogleCalendarAutoSyncEnabled_GoogleCalendarLastAttem~";
                """, suppressTransaction: true);
            migrationBuilder.CreateIndex(
                name: "IX_Users_GoogleCalendarAutoSyncEnabled_GoogleCalendarLastAttem~",
                table: "Users",
                columns: new[] { "GoogleCalendarAutoSyncEnabled", "GoogleCalendarLastAttemptAtUtc" })
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_XpAwardLogs_UserId_Source_SourceId";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_Users_GoogleCalendarAutoSyncEnabled_GoogleCalendarLastAttem~";
                """, suppressTransaction: true);
        }
    }
}
