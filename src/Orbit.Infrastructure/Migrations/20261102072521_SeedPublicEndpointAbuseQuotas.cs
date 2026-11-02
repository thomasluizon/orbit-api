using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedPublicEndpointAbuseQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[,]
                {
                    { "OnboardingAttestationCallsPerDay", "Global daily onboarding challenge and enrollment limit", "10000" },
                    { "OnboardingAttestationCallsPerHour", "Global hourly onboarding challenge and enrollment limit", "1000" },
                    { "OnboardingEnrollAttemptsPerInstallPerHour", "Hourly enrollment attempt limit per signed installation subject", "3" },
                    { "SendCodeEmailsPerDay", "Global daily code email safety limit", "10000" },
                    { "SendCodeEmailsPerIpPerDay", "Daily code email limit per resolved client IP", "100" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "OnboardingAttestationCallsPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "OnboardingAttestationCallsPerHour");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "OnboardingEnrollAttemptsPerInstallPerHour");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "SendCodeEmailsPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "SendCodeEmailsPerIpPerDay");
        }
    }
}
