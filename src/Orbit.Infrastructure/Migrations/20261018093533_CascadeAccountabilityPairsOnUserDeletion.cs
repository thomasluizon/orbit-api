using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CascadeAccountabilityPairsOnUserDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountabilityPairs_Users_AddresseeId",
                table: "AccountabilityPairs");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountabilityPairs_Users_RequesterId",
                table: "AccountabilityPairs");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountabilityPairs_Users_AddresseeId",
                table: "AccountabilityPairs",
                column: "AddresseeId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountabilityPairs_Users_RequesterId",
                table: "AccountabilityPairs",
                column: "RequesterId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountabilityPairs_Users_AddresseeId",
                table: "AccountabilityPairs");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountabilityPairs_Users_RequesterId",
                table: "AccountabilityPairs");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountabilityPairs_Users_AddresseeId",
                table: "AccountabilityPairs",
                column: "AddresseeId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountabilityPairs_Users_RequesterId",
                table: "AccountabilityPairs",
                column: "RequesterId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
