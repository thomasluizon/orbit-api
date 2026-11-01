using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetainChatConversationContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatConversationTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HasAttachment = table.Column<bool>(type: "boolean", nullable: false),
                    HasPreview = table.Column<bool>(type: "boolean", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatConversationTurns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatConversationTurns_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversationTurns_CreatedAtUtc",
                table: "ChatConversationTurns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversationTurns_UserId_ConversationId_CreatedAtUtc",
                table: "ChatConversationTurns",
                columns: new[] { "UserId", "ConversationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversationTurns_UserId_ConversationId_TurnId",
                table: "ChatConversationTurns",
                columns: new[] { "UserId", "ConversationId", "TurnId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatConversationTurns");
        }
    }
}
