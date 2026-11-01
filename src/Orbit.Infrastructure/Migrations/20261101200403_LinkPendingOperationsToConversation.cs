using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LinkPendingOperationsToConversation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "PendingAgentOperations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingAgentOperations_UserId_ConversationId_CreatedAtUtc",
                table: "PendingAgentOperations",
                columns: new[] { "UserId", "ConversationId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingAgentOperations_UserId_ConversationId_CreatedAtUtc",
                table: "PendingAgentOperations");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "PendingAgentOperations");
        }
    }
}
