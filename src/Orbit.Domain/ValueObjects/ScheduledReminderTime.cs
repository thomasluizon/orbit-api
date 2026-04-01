using Orbit.Domain.Enums;

namespace Orbit.Domain.ValueObjects;

public record ScheduledReminderTime(ScheduledReminderWhen When, TimeOnly Time);
