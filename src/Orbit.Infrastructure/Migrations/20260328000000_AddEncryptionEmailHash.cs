using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptionEmailHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add EmailHash column (initially nullable to allow migration of existing data)
            migrationBuilder.AddColumn<string>(
                name: "EmailHash",
                table: "Users",
                type: "text",
                nullable: true);

            // Drop unique index on Email (will be replaced by EmailHash index)
            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            // Drop unique index on PushSubscription.Endpoint (encrypted values are non-deterministic)
            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions");

            // Create unique index on EmailHash
            // Note: this is created after the data migration service populates EmailHash values
            migrationBuilder.CreateIndex(
                name: "IX_Users_EmailHash",
                table: "Users",
                column: "EmailHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop EmailHash index
            migrationBuilder.DropIndex(
                name: "IX_Users_EmailHash",
                table: "Users");

            // Restore unique index on PushSubscription.Endpoint
            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);

            // Restore unique index on Email
            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            // Remove EmailHash column
            migrationBuilder.DropColumn(
                name: "EmailHash",
                table: "Users");
        }
    }
}
