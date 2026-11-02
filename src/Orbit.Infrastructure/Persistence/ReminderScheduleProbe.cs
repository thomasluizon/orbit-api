namespace Orbit.Infrastructure.Persistence;

internal sealed class ReminderScheduleProbe
{
    public Guid UserId { get; private set; }
    public long Version { get; private set; }
}
