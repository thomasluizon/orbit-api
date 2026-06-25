namespace Orbit.Domain.Entities;

public class ProcessedStripeEvent : ProcessedExternalEvent
{
    public string EventId { get; private set; } = "";

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
