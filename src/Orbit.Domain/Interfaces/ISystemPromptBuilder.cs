using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Interfaces;

public record PromptBuildRequest(
    IReadOnlyList<Habit> ActiveHabits,
    IReadOnlyList<UserFact> UserFacts,
    bool HasImage = false,
    IReadOnlyList<RoutinePattern>? RoutinePatterns = null,
    IReadOnlyList<Tag>? UserTags = null,
    DateOnly? UserToday = null,
    IReadOnlyDictionary<Guid, HabitMetrics>? HabitMetrics = null,
    IReadOnlyList<Goal>? ActiveGoals = null);

public interface ISystemPromptBuilder
{
    string Build(PromptBuildRequest request);
}
