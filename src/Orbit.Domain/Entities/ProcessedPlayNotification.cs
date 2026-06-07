using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class ProcessedPlayNotification : Entity
{
    public string MessageId { get; private set; } = "";
    public DateTime ProcessedAtUtc { get; private set; }

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
