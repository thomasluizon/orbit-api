using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Persistence;

public sealed class HabitRankingReader(OrbitDbContext context, IEncryptionService encryption) : IHabitRankingReader
{
    public async Task<HabitRankingPage> ReadAsync(HabitRankingDefinition query, int offset,
        CancellationToken cancellationToken = default)
    {
        var results = await context.Database.SqlQuery<RankingSqlRow>(BuildQuery(query, offset))
            .ToListAsync(cancellationToken);
        var summary = results[0];
        var rows = results.Where(row => row.HabitId.HasValue).Select(row => new HabitRankingReadRow(
            row.HabitId!.Value, encryption.Decrypt(row.Title!), encryption.DecryptNullable(row.Emoji),
            row.ParentHabitId, row.CompletedCount!.Value)).ToArray();
        return new HabitRankingPage(rows, summary.EligibleCount, summary.TotalCount, summary.Fingerprint);
    }

    internal FormattableString BuildQuery(HabitRankingDefinition query, int offset)
    {
        var idFilter = context.Database.ProviderName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => "SELECT upper(value) FROM json_each({3})",
            "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                "SELECT CAST(value AS uuid) FROM jsonb_array_elements_text(CAST({3} AS jsonb))",
            _ => throw new NotSupportedException("Habit ranking requires a relational provider.")
        };
        var positiveValue = context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? "CAST(l.\"Value\" AS numeric) > 0" : "l.\"Value\" > 0";
        var sql = """
            WITH eligible AS MATERIALIZED (
                SELECT "Id", "Title", "Emoji", "ParentHabitId"
                FROM "Habits"
                WHERE "UserId" = {0} AND NOT "IsDeleted" AND NOT "IsBadHabit"
                  AND "Schedule" IN ({5}, {6})
                  AND ({4} = 0 OR "Id" IN (
            """ + idFilter + """
                ))
            ), counts AS MATERIALIZED (
                SELECT e."Id", CAST(COUNT(*) AS integer) AS "CompletedCount"
                FROM eligible AS e
                JOIN "HabitLogs" AS l ON l."HabitId" = e."Id"
                WHERE NOT l."IsDeleted" AND NOT l."IsSlip" AND
            """ + " " + positiveValue + " " + """
                  AND l."Date" >= {1} AND l."Date" < {2}
                GROUP BY e."Id"
            ), ranked AS MATERIALIZED (
                SELECT e.*, c."CompletedCount"
                FROM counts AS c JOIN eligible AS e ON e."Id" = c."Id"
            ), summary AS (
                SELECT CAST((SELECT COUNT(*) FROM eligible) AS integer) AS "EligibleCount",
                    CAST(COUNT(*) AS integer) AS "TotalCount",
                    md5(COALESCE(string_agg(
                        md5(CAST("Id" AS text)) || md5("Title") ||
                        CASE WHEN "Emoji" IS NULL THEN '0' ELSE '1' || md5("Emoji") END ||
                        COALESCE(CAST("ParentHabitId" AS text), '') || ':' || CAST("CompletedCount" AS text),
                        ',' ORDER BY "Id"), '')) AS "Fingerprint"
                FROM ranked
            ), page AS (
                SELECT * FROM ranked
                ORDER BY "CompletedCount" DESC, "Id"
                LIMIT {7} OFFSET {8}
            )
            SELECT s."EligibleCount", s."TotalCount", s."Fingerprint", p."Id" AS "HabitId",
                p."Title", p."Emoji", p."ParentHabitId", p."CompletedCount"
            FROM summary AS s LEFT JOIN page AS p ON TRUE
            ORDER BY p."CompletedCount" DESC, p."Id"
            """;
        return FormattableStringFactory.Create(sql, query.UserId, query.DateFrom, query.DateToExclusive,
            JsonSerializer.Serialize(query.HabitIds), query.HabitIds.Count,
            (int)HabitSchedule.Recurring, (int)HabitSchedule.Flexible, query.PageSize, offset);
    }

    private sealed class RankingSqlRow
    {
        public int EligibleCount { get; set; }
        public int TotalCount { get; set; }
        public string Fingerprint { get; set; } = string.Empty;
        public Guid? HabitId { get; set; }
        public string? Title { get; set; }
        public string? Emoji { get; set; }
        public Guid? ParentHabitId { get; set; }
        public int? CompletedCount { get; set; }
    }
}
