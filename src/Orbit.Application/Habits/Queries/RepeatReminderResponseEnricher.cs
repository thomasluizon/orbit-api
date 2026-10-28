using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

internal static class RepeatReminderResponseEnricher
{
    public static async Task<HabitDetailResponse> EnrichAsync(HabitDetailResponse detail, Guid userId,
        IRepeatReminderReader reader, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        if (detail.RepeatReminder is not null)
            ids.Add(detail.Id);
        void Collect(HabitChildResponse child)
        {
            if (child.RepeatReminder is not null)
                ids.Add(child.Id);
            foreach (var descendant in child.Children)
                Collect(descendant);
        }
        foreach (var child in detail.Children)
            Collect(child);
        if (ids.Count == 0)
            return detail;
        var next = await reader.GetNextAsync(userId, ids, ct);
        HabitChildResponse Enrich(HabitChildResponse child) => child with
        {
            NextReminderAtUtc = next.GetValueOrDefault(child.Id),
            Children = child.Children.Select(Enrich).ToList()
        };
        return detail with
        {
            NextReminderAtUtc = next.GetValueOrDefault(detail.Id),
            Children = detail.Children.Select(Enrich).ToList()
        };
    }

    public static async Task<IReadOnlyList<HabitScheduleItem>> EnrichAsync(IReadOnlyList<HabitScheduleItem> items,
        Guid userId, IRepeatReminderReader reader, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        void Collect(HabitScheduleChildItem child)
        {
            if (child.RepeatReminder is not null)
                ids.Add(child.Id);
            foreach (var descendant in child.Children)
                Collect(descendant);
        }
        foreach (var item in items)
        {
            if (item.RepeatReminder is not null)
                ids.Add(item.Id);
            foreach (var child in item.Children)
                Collect(child);
        }
        if (ids.Count == 0)
            return items;
        var next = await reader.GetNextAsync(userId, ids, ct);
        HabitScheduleChildItem Enrich(HabitScheduleChildItem child) => child with
        {
            NextReminderAtUtc = next.GetValueOrDefault(child.Id),
            Children = child.Children.Select(Enrich).ToList()
        };
        return items.Select(item => item with
        {
            NextReminderAtUtc = next.GetValueOrDefault(item.Id),
            Children = item.Children.Select(Enrich).ToList()
        }).ToList();
    }
}
