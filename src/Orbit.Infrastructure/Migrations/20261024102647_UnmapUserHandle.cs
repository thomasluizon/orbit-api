using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnmapUserHandle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // https://github.com/thomasluizon/orbit-tickets/issues/1654 keeps the column for old instances during the release drain.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // https://github.com/thomasluizon/orbit-tickets/issues/1654 keeps the column for old instances during the release drain.
        }
    }
}
