using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecognizeVerifiedLegacyConnectorCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "ApiKeys"
                SET "OAuthFamilyId" = "Id"
                WHERE "Id" IN ('A427B057-AD89-40FF-A56C-87F4F3C51DAA', '84140EA7-8394-4A41-BEB8-2D55DE79E8D8')
                  AND "Name" = 'Claude.ai'
                  AND "OAuthFamilyId" IS NULL
                  AND NOT "IsRevoked";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
