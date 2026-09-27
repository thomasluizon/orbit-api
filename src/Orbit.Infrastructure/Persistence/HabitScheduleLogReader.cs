using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class HabitScheduleLogReader(OrbitDbContext context) : IHabitScheduleLogReader
{
    public async Task<IReadOnlyList<HabitScheduleLogDay>> ReadDaysAsync(
        IReadOnlyCollection<Guid> habitIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (habitIds.Count == 0 || from > to)
            return [];

        var idsJson = JsonSerializer.Serialize(habitIds);
        var fromIso = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toIso = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        FormattableString sql = context.Database.ProviderName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => $"""
                SELECT "HabitId", json_array(
                    json(COALESCE(json_group_array(CAST(julianday("Date") - julianday({fromIso}) AS integer)) FILTER (WHERE CAST("Value" AS numeric) > 0), '[]')),
                    json(COALESCE(json_group_array(CAST(julianday("Date") - julianday({fromIso}) AS integer)) FILTER (WHERE CAST("Value" AS numeric) = 0), '[]')),
                    json(COALESCE(json_group_array(DISTINCT CAST(julianday("Date") - julianday({fromIso}) AS integer)) FILTER (WHERE CAST("Value" AS numeric) < 0), '[]'))
                ) AS "Facts"
                FROM "HabitLogs"
                WHERE "HabitId" IN (SELECT upper(value) FROM json_each({idsJson}))
                  AND "Date" >= {fromIso} AND "Date" <= {toIso} AND NOT "IsDeleted"
                GROUP BY "HabitId"
                """,
            "Npgsql.EntityFrameworkCore.PostgreSQL" => $"""
                SELECT "HabitId", jsonb_build_array(
                    COALESCE(jsonb_agg("Date" - CAST({fromIso} AS date)) FILTER (WHERE "Value" > 0), '[]'::jsonb),
                    COALESCE(jsonb_agg("Date" - CAST({fromIso} AS date)) FILTER (WHERE "Value" = 0), '[]'::jsonb),
                    COALESCE(jsonb_agg(DISTINCT "Date" - CAST({fromIso} AS date)) FILTER (WHERE "Value" < 0), '[]'::jsonb)
                )::text AS "Facts"
                FROM "HabitLogs"
                WHERE "HabitId" IN (SELECT value::uuid FROM jsonb_array_elements_text(CAST({idsJson} AS jsonb)) AS value)
                  AND "Date" >= CAST({fromIso} AS date)
                  AND "Date" <= CAST({toIso} AS date) AND NOT "IsDeleted"
                GROUP BY "HabitId"
                """,
            _ => throw new NotSupportedException("Schedule log aggregation requires a relational provider.")
        };
        var rows = await context.Database.SqlQuery<HabitLogAggregateRow>(sql)
            .ToListAsync(cancellationToken);
        var days = new List<HabitScheduleLogDay>();
        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row.Facts);
            var facts = document.RootElement;
            var counts = new Dictionary<int, (int Completed, int Skipped)>();
            foreach (var offset in facts[0].EnumerateArray())
            {
                var day = offset.GetInt32();
                counts.TryGetValue(day, out var count);
                counts[day] = (count.Completed + 1, count.Skipped);
            }

            foreach (var offset in facts[1].EnumerateArray())
            {
                var day = offset.GetInt32();
                counts.TryGetValue(day, out var count);
                counts[day] = (count.Completed, count.Skipped + 1);
            }

            foreach (var offset in facts[2].EnumerateArray())
                counts.TryAdd(offset.GetInt32(), default);

            foreach (var (offset, count) in counts)
            {
                days.Add(new HabitScheduleLogDay(
                    row.HabitId,
                    from.AddDays(offset),
                    count.Completed,
                    count.Skipped,
                    true));
            }
        }

        return days;
    }

    public async Task<IReadOnlySet<Guid>> ReadResolvedDueDateIdsAsync(
        IReadOnlyCollection<Guid> habitIds,
        CancellationToken cancellationToken = default)
    {
        if (habitIds.Count == 0)
            return new HashSet<Guid>();

        var ids = habitIds.ToArray();
        var resolved = await (
            from log in context.HabitLogs.AsNoTracking()
            join habit in context.Habits.AsNoTracking() on log.HabitId equals habit.Id
            where ids.Contains(log.HabitId) && log.Date == habit.DueDate && log.Value >= 0
            select log.HabitId).Distinct().ToListAsync(cancellationToken);
        return resolved.ToHashSet();
    }

    public sealed class HabitLogAggregateRow
    {
        public Guid HabitId { get; set; }
        public string Facts { get; set; } = string.Empty;
    }
}
