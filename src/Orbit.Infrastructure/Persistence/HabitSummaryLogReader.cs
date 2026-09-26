using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class HabitSummaryLogReader(OrbitDbContext context) : IHabitSummaryLogReader
{
    public async Task<IReadOnlyList<HabitSummaryLogFact>> ReadAsync(
        IReadOnlyCollection<HabitSummaryLogWindow> windows,
        CancellationToken cancellationToken = default)
    {
        if (windows.Count == 0)
            return [];

        var windowsJson = JsonSerializer.Serialize(windows);
        FormattableString sql = context.Database.ProviderName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => $"""
                SELECT DISTINCT l."HabitId", l."Date", l."Value"
                FROM "HabitLogs" AS l
                JOIN json_each({windowsJson}) AS w
                  ON l."HabitId" = upper(json_extract(w.value, '$.HabitId'))
                 AND l."Date" >= json_extract(w.value, '$.From')
                 AND l."Date" <= json_extract(w.value, '$.To')
                WHERE NOT l."IsDeleted" AND l."Value" >= 0
                """,
            "Npgsql.EntityFrameworkCore.PostgreSQL" => $"""
                SELECT DISTINCT l."HabitId", l."Date"::text AS "Date", l."Value"
                FROM "HabitLogs" AS l
                JOIN jsonb_to_recordset(CAST({windowsJson} AS jsonb))
                  AS w("HabitId" uuid, "From" date, "To" date)
                  ON l."HabitId" = w."HabitId"
                 AND l."Date" >= w."From"
                 AND l."Date" <= w."To"
                WHERE NOT l."IsDeleted" AND l."Value" >= 0
                """,
            _ => throw new NotSupportedException("Summary log windows require a relational provider.")
        };

        var rows = await context.Database.SqlQuery<HabitSummaryLogRow>(sql)
            .ToListAsync(cancellationToken);
        return rows.Select(row => new HabitSummaryLogFact(
            row.HabitId,
            DateOnly.Parse(row.Date, CultureInfo.InvariantCulture),
            row.Value)).ToList();
    }

    public sealed class HabitSummaryLogRow
    {
        public Guid HabitId { get; set; }
        public string Date { get; set; } = string.Empty;
        public decimal Value { get; set; }
    }
}
