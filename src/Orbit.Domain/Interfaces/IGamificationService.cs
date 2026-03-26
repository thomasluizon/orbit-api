namespace Orbit.Domain.Interfaces;

public interface IGamificationService
{
    Task ProcessHabitLogged(Guid userId, Guid habitId, CancellationToken ct = default);
    Task ProcessHabitCreated(Guid userId, CancellationToken ct = default);
    Task ProcessGoalCreated(Guid userId, CancellationToken ct = default);
    Task ProcessGoalCompleted(Guid userId, CancellationToken ct = default);
}
