using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhraseReaderAndMascotMotion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.AddColumn<bool>(
                name: "MascotMotionEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PhraseReadsLocalDate",
                table: "Users",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhraseReadsToday",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.InsertData(
                table: "AppFeatureFlags",
                columns: new[] { "Key", "Description", "Enabled", "PlanRequirement", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { "orbit_guided_flows", "Roll out the guided create, edit and onboarding flows", false, null, new DateTime(2026, 11, 12, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "phrase_reader_ai_disabled", "Kill switch for the AI read of a habit phrase; the clients ask every question instead", false, null, new DateTime(2026, 11, 12, 0, 0, 0, 0, DateTimeKind.Utc) }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.DeleteData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "orbit_guided_flows");

            migrationBuilder.DeleteData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "phrase_reader_ai_disabled");

            migrationBuilder.DropColumn(
                name: "MascotMotionEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PhraseReadsLocalDate",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PhraseReadsToday",
                table: "Users");
        }
    }
}
