using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class GoogleCalendarSyncSuggestion : Entity
{
    public Guid UserId { get; private set; }
    public string GoogleEventId { get; private set; } = null!;
    public string Title { get; private set; } = null!;
    public DateTime StartDateUtc { get; private set; }
    public string RawEventJson { get; private set; } = null!;
    public DateTime DiscoveredAtUtc { get; private set; }
    public DateTime? DismissedAtUtc { get; private set; }
    public DateTime? ImportedAtUtc { get; private set; }
    public Guid? ImportedHabitId { get; private set; }

    private GoogleCalendarSyncSuggestion() { }

    public static GoogleCalendarSyncSuggestion Create(
        Guid userId,
        string googleEventId,
        string title,
        DateTime startDateUtc,
        string rawEventJson,
        DateTime discoveredAtUtc)
    {
        return new GoogleCalendarSyncSuggestion
        {
            UserId = userId,
            GoogleEventId = googleEventId,
            Title = title,
            StartDateUtc = startDateUtc,
            RawEventJson = rawEventJson,
            DiscoveredAtUtc = discoveredAtUtc
        };
    }

    public void MarkDismissed(DateTime utcNow)
    {
        DismissedAtUtc = utcNow;
    }

    public void MarkImported(Guid habitId, DateTime utcNow)
    {
        ImportedAtUtc = utcNow;
        ImportedHabitId = habitId;
    }
}
