namespace Orbit.Application.Gamification.Models;

public sealed record LoggedHabitLog(Guid HabitId, Guid Id, DateOnly Date, decimal Value, bool IsDeleted);
