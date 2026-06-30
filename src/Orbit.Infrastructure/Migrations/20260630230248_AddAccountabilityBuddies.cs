using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountabilityBuddies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountabilityPairs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddresseeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Cadence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountabilityPairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountabilityPairs_Users_AddresseeId",
                        column: x => x.AddresseeId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountabilityPairs_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccountabilityCheckIns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PairId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountabilityCheckIns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountabilityCheckIns_AccountabilityPairs_PairId",
                        column: x => x.PairId,
                        principalTable: "AccountabilityPairs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountabilityPairHabits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PairId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    HabitId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountabilityPairHabits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountabilityPairHabits_AccountabilityPairs_PairId",
                        column: x => x.PairId,
                        principalTable: "AccountabilityPairs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountabilityPairHabits_Habits_HabitId",
                        column: x => x.HabitId,
                        principalTable: "Habits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityCheckIns_PairId_CreatedAtUtc",
                table: "AccountabilityCheckIns",
                columns: new[] { "PairId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityCheckIns_PairId_UserId_Date",
                table: "AccountabilityCheckIns",
                columns: new[] { "PairId", "UserId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityPairHabits_HabitId",
                table: "AccountabilityPairHabits",
                column: "HabitId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityPairHabits_PairId_UserId_HabitId",
                table: "AccountabilityPairHabits",
                columns: new[] { "PairId", "UserId", "HabitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityPairs_AddresseeId",
                table: "AccountabilityPairs",
                column: "AddresseeId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityPairs_RequesterId",
                table: "AccountabilityPairs",
                column: "RequesterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountabilityCheckIns");

            migrationBuilder.DropTable(
                name: "AccountabilityPairHabits");

            migrationBuilder.DropTable(
                name: "AccountabilityPairs");
        }
    }
}
