using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillHasCompletedOnboardingForExistingUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill onboarding completion for accounts that predate the HasCompletedOnboarding
            // column (added defaulting to false with no backfill) or that abandoned onboarding after
            // creating habits. Any user that already owns at least one live habit has effectively
            // onboarded, so mark them complete to stop the retained overlay re-onboarding them.
            migrationBuilder.Sql(
                """
                UPDATE "Users" u
                SET "HasCompletedOnboarding" = true
                WHERE u."HasCompletedOnboarding" = false
                  AND EXISTS (
                      SELECT 1 FROM "Habits" h
                      WHERE h."UserId" = u."Id" AND h."DeletedAtUtc" IS NULL
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data backfill: the pre-update HasCompletedOnboarding values are not recoverable.
        }
    }
}
