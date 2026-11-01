using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersistServerConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "ConversationTurns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageContentType",
                table: "ConversationTurns",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageObjectKey",
                table: "ConversationTurns",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageSha256",
                table: "ConversationTurns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewOutcomesJson",
                table: "ConversationTurns",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ToolResultsJson",
                table: "ConversationTurns",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<long>(
                name: "TurnOrder",
                table: "ConversationTurns",
                type: "bigint",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "astra_conversation_recording",
                column: "Description",
                value: "Store encrypted Astra conversations and private images for server history after privacy disclosure is published");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurns_ConversationId",
                table: "ConversationTurns",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurns_UserId_ConversationId_TurnOrder",
                table: "ConversationTurns",
                columns: new[] { "UserId", "ConversationId", "TurnOrder" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationTurns_ConversationId",
                table: "ConversationTurns");

            migrationBuilder.DropIndex(
                name: "IX_ConversationTurns_UserId_ConversationId_TurnOrder",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "ImageContentType",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "ImageObjectKey",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "ImageSha256",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "PreviewOutcomesJson",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "ToolResultsJson",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "TurnOrder",
                table: "ConversationTurns");

            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "astra_conversation_recording",
                column: "Description",
                value: "Record encrypted Astra conversations for failure diagnosis for 30 days after privacy disclosure is published");
        }
    }
}
