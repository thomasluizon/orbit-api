using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations;

/// <inheritdoc />
public partial class UnmapMarketingBroadcastDeliveries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // https://github.com/thomasluizon/orbit-tickets/issues/1956 preserves the unused table until a later release satisfies the rolling migration deployment barrier.
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // https://github.com/thomasluizon/orbit-tickets/issues/1956 preserves the same schema in both deployment directions.
    }
}
