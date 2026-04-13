using System.Text;
using Orbit.Domain.Interfaces;
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
            new StructuringStrategySection(),
            new ActiveHabitsSection(),
            new ActiveGoalsSection(),
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
        foreach (var section in _sections.Where(s => s.ShouldInclude(context)).OrderBy(s => s.Order))
        {
            sb.Append(section.Build(context));
        }
        return sb.ToString();
    }

    // ISystemPromptBuilder implementation
    string ISystemPromptBuilder.Build(PromptBuildRequest request)
    {
        var context = new PromptContext(
            request.ActiveHabits,
            request.UserFacts,
            request.HasImage,
            request.RoutinePatterns,
            request.UserTags,
            request.UserToday,
            request.HabitMetrics,
            request.ActiveGoals);
        return Build(context);
    }

}
