namespace Orbit.Domain.Entities;

public sealed class ProcessedAppStoreNotification : ProcessedExternalEvent
{
    public Guid NotificationUuid { get; private set; }
    public string Environment { get; private set; } = "";

    private ProcessedAppStoreNotification() { }

    public static ProcessedAppStoreNotification Create(Guid notificationUuid, string environment)
    {
        if (notificationUuid == Guid.Empty || environment is not ("Sandbox" or "Production"))
            throw new ArgumentException("Notification UUID and App Store environment are required.");
        return new ProcessedAppStoreNotification { NotificationUuid = notificationUuid, Environment = environment, ProcessedAtUtc = DateTime.UtcNow };
    }
}
