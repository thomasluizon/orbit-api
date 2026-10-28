using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

public static class RepeatReminderDoseLoader
{
    public static IQueryable<HabitLog> LatestCandidates(IQueryable<HabitLog> logs, List<Guid> excludedIds) =>
        logs.Where(l => l.Value > 0 && !excludedIds.Contains(l.Id)
                && (l.LoggedOnLocalDate == null || l.Date == l.LoggedOnLocalDate))
            .GroupBy(l => l.HabitId)
            .Select(g => g.OrderByDescending(l => l.TakenAtUtc ?? l.CreatedAtUtc).ThenByDescending(l => l.Id).First());

    public static async Task<Dictionary<Guid, DateTime>> LoadLatestAsync(
        IGenericRepository<HabitLog> logs, IReadOnlyDictionary<Guid, TimeZoneInfo> zones, CancellationToken ct)
    {
        var pendingIds = zones.Keys.ToList();
        var excludedIds = new List<Guid>();
        var anchors = new Dictionary<Guid, DateTime>();
        while (pendingIds.Count > 0)
        {
            var candidates = await logs.ProjectAsync(l => pendingIds.Contains(l.HabitId),
                query => LatestCandidates(query, excludedIds), ct);
            pendingIds = [];
            foreach (var dose in candidates)
            {
                var loggedDate = dose.LoggedOnLocalDate ?? DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dose.CreatedAtUtc, DateTimeKind.Utc), zones[dose.HabitId]));
                if (dose.Date == loggedDate)
                    anchors[dose.HabitId] = dose.TakenAtUtc ?? dose.CreatedAtUtc;
                else
                {
                    excludedIds.Add(dose.Id);
                    pendingIds.Add(dose.HabitId);
                }
            }
        }
        return anchors;
    }
}
