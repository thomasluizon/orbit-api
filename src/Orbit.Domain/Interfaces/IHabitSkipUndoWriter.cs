using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface IHabitSkipUndoWriter
{
    Task SaveAsync(Habit habit, DateTime expectedUpdatedAtUtc, CancellationToken cancellationToken);
}
