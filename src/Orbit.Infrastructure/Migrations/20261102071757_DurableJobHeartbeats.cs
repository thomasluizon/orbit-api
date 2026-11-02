using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableJobHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundJobHeartbeats",
                columns: table => new
                {
                    JobName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSuccessfulTickAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobHeartbeats", x => x.JobName);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundJobHeartbeats");
        }
    }
}
