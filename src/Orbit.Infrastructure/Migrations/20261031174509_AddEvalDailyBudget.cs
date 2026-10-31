using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEvalDailyBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EvalDateUtc",
                table: "AiBudgetCalls",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiBudgetCalls_EvalDateUtc",
                table: "AiBudgetCalls",
                column: "EvalDateUtc",
                filter: "\"EvalDateUtc\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiBudgetCalls_EvalDateUtc",
                table: "AiBudgetCalls");

            migrationBuilder.DropColumn(
                name: "EvalDateUtc",
                table: "AiBudgetCalls");
        }
    }
}
