using System.Text;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;
using Orbit.Infrastructure.Services.Prompts.Sections.Static;

namespace Orbit.Infrastructure.Services;

public class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly IReadOnlyList<IPromptSection> _staticSections;
    private readonly IReadOnlyList<IPromptSection> _accountSections;

    public SystemPromptBuilder()
    {
        _staticSections =
        [
            new CoreIdentitySection(),
            new EncouragingToneSection(),
            new GlobalRulesSection(),
            new CrisisResponseSection(),
            new StructuringStrategySection(),
            new ClarificationGuidanceSection(),
            new ToolFailureSection(),
        ];
        _accountSections =
        [
            new ActiveHabitsSection(),
            new ActiveGoalsSection(),
            new UserTagsSection(),
            new RoutinePatternsSection(),
            new HabitCountSection(),
        ];
    }

    public string BuildStatic(PromptBuildRequest request) =>
        Render(_staticSections, ToContext(request)) + Environment.NewLine + GlobalRulesSection.BuildFormat(ToContext(request));

    public string BuildDynamic(PromptBuildRequest request) => BuildAccountContext(request) + BuildTurnContext(request);

    public string BuildAccountContext(PromptBuildRequest request) => Render(_accountSections, ToContext(request));

    public string BuildTurnContext(PromptBuildRequest request) =>
        Render([new TodayDateSection(), new ImageInstructionsSection()], ToContext(request));

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
            request.HasImage,
            request.RoutinePatterns,
            request.UserTags,
            request.UserToday,
            request.HabitMetrics,
            request.ActiveGoals,
            request.IsHabitIndexPartial,
            request.TodayHabitIds,
            request.OverdueHabitIds,
            request.DoneTodayHabitIds,
            request.BlockFirstComposition);
}
