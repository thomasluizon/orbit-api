using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class ProcessedStripeEvent : Entity
{
    public string EventId { get; private set; } = "";
    public DateTime ProcessedAtUtc { get; private set; }

    private ProcessedStripeEvent() { }

    public static ProcessedStripeEvent Create(string eventId)
    {
        return new ProcessedStripeEvent
        {
            EventId = eventId,
            ProcessedAtUtc = DateTime.UtcNow,
        };
    }
}
