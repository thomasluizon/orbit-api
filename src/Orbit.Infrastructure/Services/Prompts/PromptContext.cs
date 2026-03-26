using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Services.Prompts;

public record PromptContext(
    IReadOnlyList<Habit> ActiveHabits,
    IReadOnlyList<UserFact> UserFacts,
    bool HasImage,
    IReadOnlyList<RoutinePattern>? RoutinePatterns,
    IReadOnlyList<Tag>? UserTags,
    DateOnly? UserToday,
    IReadOnlyDictionary<Guid, HabitMetrics>? HabitMetrics,
    IReadOnlyList<Goal>? ActiveGoals = null);
