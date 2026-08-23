using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RaiseMinSupportedVersionFloor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "MinSupportedVersion",
                column: "Value",
                value: "1.3.11");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "MinSupportedVersion",
                column: "Value",
                value: "0.0.0");
        }
    }
}
