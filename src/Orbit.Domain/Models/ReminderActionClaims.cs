namespace Orbit.Domain.Models;

public sealed record ReminderActionClaims(Guid UserId, Guid HabitId, Guid ReminderId, DateTime ReminderAtUtc, DateTime ExpiresAtUtc);
