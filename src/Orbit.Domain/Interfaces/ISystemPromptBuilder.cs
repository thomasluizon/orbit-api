using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Interfaces;

public interface ISystemPromptBuilder
{
    string Build(
        IReadOnlyList<Habit> activeHabits,
        IReadOnlyList<UserFact> userFacts,
        bool hasImage = false,
        IReadOnlyList<RoutinePattern>? routinePatterns = null,
        IReadOnlyList<Tag>? userTags = null,
        DateOnly? userToday = null,
        IReadOnlyDictionary<Guid, HabitMetrics>? habitMetrics = null);
}
