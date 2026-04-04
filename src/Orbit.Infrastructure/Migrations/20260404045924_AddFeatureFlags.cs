using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "Tags",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Tags",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Tags",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Notifications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "Habits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Habits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Habits",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "HabitLogs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "Goals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Goals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Goals",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "GoalProgressLogs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "ChecklistTemplates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "AppFeatureFlags",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PlanRequirement = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppFeatureFlags", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ContentBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Locale = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentBlocks", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AppFeatureFlags",
                columns: new[] { "Key", "Description", "Enabled", "PlanRequirement", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { "ai_chat", "AI chat assistant", true, "Pro", new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "ai_retrospective", "AI retrospective analysis", true, "Pro", new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "ai_summary", "AI daily summary", true, "Pro", new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "api_keys", "Personal API keys", true, "Pro", new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "bulk_operations", "Bulk create/delete/log habits", true, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "calendar_integration", "Google Calendar integration", true, "Pro", new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "checklist_templates", "Reusable checklist templates", true, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "goal_tracking", "Goal tracking with progress", true, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "habit_duplication", "Duplicate habits", true, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "offline_mode", "Enable offline mode with background sync", true, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "push_notifications", "Push notification reminders", true, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "scheduled_reminders", "Custom scheduled reminders", true, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "slip_alerts", "Slip detection alerts", true, "Pro", new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "sub_habits", "Sub-habit nesting", true, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tags_UserId_IsDeleted",
                table: "Tags",
                columns: new[] { "UserId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Habits_UserId_IsDeleted",
                table: "Habits",
                columns: new[] { "UserId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Goals_UserId_IsDeleted",
                table: "Goals",
                columns: new[] { "UserId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentBlocks_Key_Locale",
                table: "ContentBlocks",
                columns: new[] { "Key", "Locale" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppFeatureFlags");

            migrationBuilder.DropTable(
                name: "ContentBlocks");

            migrationBuilder.DropIndex(
                name: "IX_Tags_UserId_IsDeleted",
                table: "Tags");

            migrationBuilder.DropIndex(
                name: "IX_Habits_UserId_IsDeleted",
                table: "Habits");

            migrationBuilder.DropIndex(
                name: "IX_Goals_UserId_IsDeleted",
                table: "Goals");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "HabitLogs");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Goals");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Goals");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Goals");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "GoalProgressLogs");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "ChecklistTemplates");
        }
    }
}
