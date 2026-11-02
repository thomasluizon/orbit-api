using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersistOAuthClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.AddColumn<string>(
                name: "PartitionKey",
                table: "OAuthStates",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OAuthClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RedirectUris = table.Column<string[]>(type: "text[]", nullable: false),
                    AuthorizedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthClients", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_Kind_PartitionKey_ExpiresAtUtc",
                table: "OAuthStates",
                columns: new[] { "Kind", "PartitionKey", "ExpiresAtUtc" })
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OAuthClients");

            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_OAuthStates_Kind_PartitionKey_ExpiresAtUtc\";", suppressTransaction: true);
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.DropColumn(
                name: "PartitionKey",
                table: "OAuthStates");
        }
    }
}
