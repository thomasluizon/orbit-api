using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedOnboardingBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[,]
                {
                    { "OnboardingCallsPerDay", "Global daily anonymous onboarding call limit", "1000" },
                    { "OnboardingInstallCallsPerHour", "Hourly anonymous onboarding call limit per verified install", "10" },
                    { "OnboardingIpCallsPerHour", "Hourly anonymous onboarding call limit per IP address", "10" },
                    { "OnboardingTokensPerDay", "Global daily anonymous onboarding token limit", "16384000" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "OnboardingCallsPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "OnboardingInstallCallsPerHour");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "OnboardingIpCallsPerHour");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "OnboardingTokensPerDay");
        }
    }
}
