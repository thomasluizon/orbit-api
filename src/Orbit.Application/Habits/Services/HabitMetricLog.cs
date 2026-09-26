namespace Orbit.Application.Habits.Services;

public sealed record HabitMetricLog(Guid HabitId, DateOnly Date, decimal Value, bool IsDeleted);
