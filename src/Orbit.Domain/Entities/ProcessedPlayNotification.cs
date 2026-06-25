namespace Orbit.Domain.Entities;

public class ProcessedPlayNotification : ProcessedExternalEvent
{
    public string MessageId { get; private set; } = "";

    private ProcessedPlayNotification() { }

    public static ProcessedPlayNotification Create(string messageId)
    {
        return new ProcessedPlayNotification
        {
            MessageId = messageId,
            ProcessedAtUtc = DateTime.UtcNow,
        };
    }
}
