using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDesktopPushRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApnsTopic",
                table: "PushSubscriptions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeviceId",
                table: "PushSubscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_UserId_DeviceId_P256dh",
                table: "PushSubscriptions",
                columns: new[] { "UserId", "DeviceId", "P256dh" },
                unique: true,
                filter: "\"DeviceId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_UserId_DeviceId_P256dh",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "ApnsTopic",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "PushSubscriptions");
        }
    }
}
