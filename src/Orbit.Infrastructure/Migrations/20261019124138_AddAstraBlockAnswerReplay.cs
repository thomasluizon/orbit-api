using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAstraBlockAnswerReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnswerJson",
                table: "IssuedAstraBlocks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommittedResponseJson",
                table: "IssuedAstraBlocks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReservedAllowanceDate",
                table: "IssuedAstraBlocks",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseJson",
                table: "IssuedAstraBlocks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnswerJson",
                table: "IssuedAstraBlocks");

            migrationBuilder.DropColumn(
                name: "CommittedResponseJson",
                table: "IssuedAstraBlocks");

            migrationBuilder.DropColumn(
                name: "ReservedAllowanceDate",
                table: "IssuedAstraBlocks");

            migrationBuilder.DropColumn(
                name: "ResponseJson",
                table: "IssuedAstraBlocks");
        }
    }
}
