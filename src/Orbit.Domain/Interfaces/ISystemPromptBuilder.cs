using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Interfaces;

/// <param name="ChangePlan">
/// True on the <c>ai_change_plan</c> path: <see cref="ISystemPromptBuilder.BuildStatic"/> returns the one short prompt
/// (identity, act-or-ask rule, proposal, language, grounding, safety and habit model) in place of the rule sections.
/// </param>
public record PromptBuildRequest(
    IReadOnlyList<Habit> ActiveHabits,
    bool HasImage = false,
    IReadOnlyList<RoutinePattern>? RoutinePatterns = null,
    IReadOnlyList<Tag>? UserTags = null,
    DateOnly? UserToday = null,
    IReadOnlyDictionary<Guid, HabitMetrics>? HabitMetrics = null,
    IReadOnlyList<Goal>? ActiveGoals = null,
    bool IsHabitIndexPartial = false,
    IReadOnlySet<Guid>? TodayHabitIds = null,
    IReadOnlySet<Guid>? OverdueHabitIds = null,
    IReadOnlySet<Guid>? DoneTodayHabitIds = null,
    bool BlockFirstComposition = false,
    bool ChangePlan = false);

public interface ISystemPromptBuilder
{
    /// <summary>
    /// Builds the shared prefix of the system prompt (identity, global rules, structuring strategy,
    /// clarification guidance and composition format). Identical for users and requests with the
    /// same composition mode; emitted before dynamic content to preserve prompt-cache reuse.
    /// </summary>
    string BuildStatic(PromptBuildRequest request);

    /// <summary>
    /// Builds the per-user, per-request tail of the system prompt (habit and goal index, tags,
    /// routine patterns, today's date, image instructions). Emitted after all static content
    /// so it never poisons the cacheable prefix.
    /// </summary>
    string BuildDynamic(PromptBuildRequest request);

    /// <summary>Builds the account indexes before date, image, preview and conversation state.</summary>
    string BuildAccountContext(PromptBuildRequest request);

    /// <summary>Builds the current account-local date and image instructions after the account indexes.</summary>
    string BuildTurnContext(PromptBuildRequest request);
}
