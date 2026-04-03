using System.Text;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;
using Orbit.Infrastructure.Services.Prompts.Sections.Static;

namespace Orbit.Infrastructure.Services;

public class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly IReadOnlyList<IPromptSection> _sections;

    public SystemPromptBuilder()
    {
        _sections =
        [
            new CoreIdentitySection(),
            new GlobalRulesSection(),
            new ActiveHabitsSection(),
            new UserTagsSection(),
            new UserFactsSection(),
            new RoutinePatternsSection(),
            new TodayDateSection(),
            new ImageInstructionsSection(),
            new HabitCountSection(),
        ];
    }

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        foreach (var section in _sections.OrderBy(s => s.Order).Where(s => s.ShouldInclude(context)))
        {
            sb.Append(section.Build(context));
        }
        return sb.ToString();
    }

    // ISystemPromptBuilder implementation
    string ISystemPromptBuilder.Build(
        IReadOnlyList<Habit> activeHabits,
        IReadOnlyList<UserFact> userFacts,
        bool hasImage,
        IReadOnlyList<RoutinePattern>? routinePatterns,
        IReadOnlyList<Tag>? userTags,
        DateOnly? userToday,
        IReadOnlyDictionary<Guid, HabitMetrics>? habitMetrics,
        IReadOnlyList<Goal>? activeGoals)
    {
        var context = new PromptContext(activeHabits, userFacts, hasImage, routinePatterns, userTags, userToday, habitMetrics, activeGoals);
        return Build(context);
    }

}
