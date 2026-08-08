using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecordAstraChatTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiUsageDaily_Date_Model_Purpose",
                table: "AiUsageDaily");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "AiUsageDaily",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiUsageDaily_Date_Model_Purpose_UserId"
                ON "AiUsageDaily" ("Date", "Model", "Purpose", "UserId") NULLS NOT DISTINCT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AiUsageDaily" AS target
                SET
                    "Calls" = totals."Calls",
                    "CachedTokens" = totals."CachedTokens",
                    "PromptTokens" = totals."PromptTokens",
                    "CompletionTokens" = totals."CompletionTokens",
                    "TotalTokens" = totals."TotalTokens",
                    "CostUsd" = totals."CostUsd"
                FROM (
                    SELECT
                        "Date",
                        "Model",
                        "Purpose",
                        SUM("Calls")::bigint AS "Calls",
                        SUM("CachedTokens")::bigint AS "CachedTokens",
                        SUM("PromptTokens")::bigint AS "PromptTokens",
                        SUM("CompletionTokens")::bigint AS "CompletionTokens",
                        SUM("TotalTokens")::bigint AS "TotalTokens",
                        SUM("CostUsd") AS "CostUsd"
                    FROM "AiUsageDaily"
                    GROUP BY "Date", "Model", "Purpose"
                ) AS totals
                WHERE target."Id" = (
                    SELECT source."Id"
                    FROM "AiUsageDaily" AS source
                    WHERE source."Date" = totals."Date"
                        AND source."Model" = totals."Model"
                        AND source."Purpose" = totals."Purpose"
                    ORDER BY source."Id"
                    LIMIT 1
                );

                DELETE FROM "AiUsageDaily" AS target
                USING (
                    SELECT
                        "Id",
                        ROW_NUMBER() OVER (
                            PARTITION BY "Date", "Model", "Purpose"
                            ORDER BY "Id") AS "RowNumber"
                    FROM "AiUsageDaily"
                ) AS ranked
                WHERE target."Id" = ranked."Id"
                    AND ranked."RowNumber" > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_AiUsageDaily_Date_Model_Purpose_UserId",
                table: "AiUsageDaily");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "AiUsageDaily");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageDaily_Date_Model_Purpose",
                table: "AiUsageDaily",
                columns: new[] { "Date", "Model", "Purpose" },
                unique: true);
        }
    }
}
