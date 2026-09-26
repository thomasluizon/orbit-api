using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Services;

/// <summary>Shares the schedule rows used by streak and achievement checks within one request.</summary>
public sealed class HabitScheduleSnapshotStore(IGenericRepository<Habit> habits)
{
    private readonly Dictionary<Guid, Task<IReadOnlyList<HabitScheduleSnapshot>>> _snapshots = [];

    public Task<IReadOnlyList<HabitScheduleSnapshot>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_snapshots)
        {
            if (!_snapshots.TryGetValue(userId, out var snapshot))
            {
                snapshot = habits.ProjectAsync(
                    habit => habit.UserId == userId
                        && (!habit.IsBadHabit
                            || (!habit.IsCompleted && !habit.IsGeneral && habit.ParentHabitId == null)),
                    HabitScheduleProjection.Select,
                    cancellationToken);
                _snapshots.Add(userId, snapshot);
            }

            return snapshot;
        }
    }

    public void Invalidate()
    {
        lock (_snapshots)
            _snapshots.Clear();
    }
}
