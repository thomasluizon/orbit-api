using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayBillingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlayPurchaseToken",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubscriptionSource",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"SubscriptionSource\" = 0 WHERE \"StripeSubscriptionId\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlayPurchaseToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SubscriptionSource",
                table: "Users");
        }
    }
}
