using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface IHabitSchedulePageLoader
{
    Task LoadAsync(
        IReadOnlyCollection<Habit> habits,
        DateOnly logFrom,
        DateOnly logTo,
        CancellationToken cancellationToken = default);
}
