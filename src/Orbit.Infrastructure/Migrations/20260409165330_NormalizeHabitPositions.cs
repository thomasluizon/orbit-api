using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeHabitPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-off normalization: rewrite Position as contiguous 0..N-1 within each
            // (UserId, ParentHabitId) group, ordered by current position (nulls last) then creation time.
            // Fixes corrupted / sparse / NULL positions introduced by the old reorder bug.
            migrationBuilder.Sql(@"
                WITH ordered AS (
                    SELECT
                        ""Id"",
                        ROW_NUMBER() OVER (
                            PARTITION BY ""UserId"", ""ParentHabitId""
                            ORDER BY
                                CASE WHEN ""Position"" IS NULL THEN 1 ELSE 0 END,
                                ""Position"" ASC,
                                ""CreatedAtUtc"" ASC,
                                ""Id"" ASC
                        ) - 1 AS new_position
                    FROM ""Habits""
                    WHERE ""IsDeleted"" = FALSE
                )
                UPDATE ""Habits"" h
                SET ""Position"" = o.new_position
                FROM ordered o
                WHERE h.""Id"" = o.""Id"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data normalization -- no down migration.
        }
    }
}
