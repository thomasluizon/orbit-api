using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentReliabilityPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "ApiKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReadOnly",
                table: "ApiKeys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Scopes",
                table: "ApiKeys",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.CreateTable(
                name: "AgentAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Surface = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AuthMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RiskClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PolicyDecision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OutcomeStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TargetName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RedactedArguments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingAgentOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OperationFingerprint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Surface = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RiskClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConfirmationRequirement = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConfirmationTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StepUpSatisfiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAgentOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAuditLogs_CapabilityId_CreatedAtUtc",
                table: "AgentAuditLogs",
                columns: new[] { "CapabilityId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAuditLogs_UserId_CreatedAtUtc",
                table: "AgentAuditLogs",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAgentOperations_UserId_CapabilityId",
                table: "PendingAgentOperations",
                columns: new[] { "UserId", "CapabilityId" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAgentOperations_UserId_OperationFingerprint",
                table: "PendingAgentOperations",
                columns: new[] { "UserId", "OperationFingerprint" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentAuditLogs");

            migrationBuilder.DropTable(
                name: "PendingAgentOperations");

            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "IsReadOnly",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "ApiKeys");
        }
    }
}
