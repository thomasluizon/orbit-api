using System.Text;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;
using Orbit.Infrastructure.Services.Prompts.Sections.Static;

namespace Orbit.Infrastructure.Services;

public class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly IReadOnlyList<IPromptSection> _staticSections;
    private readonly IReadOnlyList<IPromptSection> _dynamicSections;

    public SystemPromptBuilder()
    {
        _staticSections =
        [
            new CoreIdentitySection(),
            new EncouragingToneSection(),
            new GlobalRulesSection(),
            new StructuringStrategySection(),
            new ClarificationGuidanceSection(),
        ];
        _dynamicSections =
        [
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

    public string BuildStatic(PromptBuildRequest request) => Render(_staticSections, ToContext(request));

    public string BuildDynamic(PromptBuildRequest request) => Render(_dynamicSections, ToContext(request));

    private static string Render(IEnumerable<IPromptSection> sections, PromptContext context)
    {
        var sb = new StringBuilder();
        foreach (var section in sections.Where(s => s.ShouldInclude(context)).OrderBy(s => s.Order))
        {
            sb.Append(section.Build(context));
        }
        return sb.ToString();
    }

    private static PromptContext ToContext(PromptBuildRequest request) =>
        new(
            request.ActiveHabits,
            request.UserFacts,
            request.HasImage,
            request.RoutinePatterns,
            request.UserTags,
            request.UserToday,
            request.HabitMetrics,
            request.ActiveGoals);
}
