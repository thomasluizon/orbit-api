using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddXminToReferral : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // xmin is a PostgreSQL system column; Npgsql emits no DDL for it, so this operation
            // runs as a no-op at the SQL level. See https://www.npgsql.org/efcore/modeling/concurrency.html
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Referrals",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Referrals");
        }
    }
}
