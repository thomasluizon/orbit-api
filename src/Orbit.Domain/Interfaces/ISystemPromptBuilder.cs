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
    /// <summary>
    /// Builds the request-invariant prefix of the system prompt (identity, global rules,
    /// structuring strategy, clarification guidance). Identical for every user and request, so it
    /// forms the cacheable span of the OpenAI prompt and must be emitted before any dynamic content.
    /// </summary>
    string BuildStatic(PromptBuildRequest request);

    /// <summary>
    /// Builds the per-user, per-request tail of the system prompt (habit and goal index, tags,
    /// facts, routine patterns, today's date, image instructions). Emitted after all static content
    /// so it never poisons the cacheable prefix.
    /// </summary>
    string BuildDynamic(PromptBuildRequest request);
}
