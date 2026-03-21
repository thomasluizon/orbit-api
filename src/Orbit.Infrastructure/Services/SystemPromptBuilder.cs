using System.Text;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Auto;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;
using Orbit.Infrastructure.Services.Prompts.Sections.Static;

namespace Orbit.Infrastructure.Services;

public class SystemPromptBuilder
{
    private readonly IReadOnlyList<IPromptSection> _sections;

    public SystemPromptBuilder(ActionDiscoveryService discovery)
    {
        _sections =
        [
            new CoreIdentitySection(),
            new ActionCapabilitiesSection(discovery),
            new GlobalRulesSection(),
            new ActiveHabitsSection(),
            new UserTagsSection(),
            new UserFactsSection(),
            new RoutinePatternsSection(),
            new TodayDateSection(),
            new ImageInstructionsSection(),
            new HabitCountSection(),
            new ActionSchemaSection(discovery),
            new ConversationalExamplesSection(),
        ];
    }

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        foreach (var section in _sections.OrderBy(s => s.Order))
        {
            if (section.ShouldInclude(context))
                sb.Append(section.Build(context));
        }
        return sb.ToString();
    }

    // Static facade for backward compatibility
    private static SystemPromptBuilder? _instance;
    private static readonly object _lock = new();

    public static string BuildSystemPrompt(
        IReadOnlyList<Habit> activeHabits,
        IReadOnlyList<UserFact> userFacts,
        bool hasImage = false,
        IReadOnlyList<RoutinePattern>? routinePatterns = null,
        IReadOnlyList<Tag>? userTags = null,
        DateOnly? userToday = null,
        IReadOnlyDictionary<Guid, HabitMetrics>? habitMetrics = null)
    {
        if (_instance is null)
        {
            lock (_lock)
            {
                _instance ??= new SystemPromptBuilder(new ActionDiscoveryService());
            }
        }

        var context = new PromptContext(activeHabits, userFacts, hasImage, routinePatterns, userTags, userToday, habitMetrics);
        return _instance.Build(context);
    }
}
