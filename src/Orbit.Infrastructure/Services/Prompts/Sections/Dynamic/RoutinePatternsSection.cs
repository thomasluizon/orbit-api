using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class RoutinePatternsSection : IPromptSection
{
    public int Order => 600;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Your Understanding of This User's Routine");
        if (context.RoutinePatterns is null || context.RoutinePatterns.Count == 0)
        {
            sb.AppendLine("(no routine patterns detected yet)");
        }
        else
        {
            foreach (var pattern in context.RoutinePatterns)
                sb.AppendLine($"- \"{pattern.HabitTitle}\": {pattern.Description} (confidence: {pattern.Confidence}, consistency: {pattern.ConsistencyScore:P0})");
            sb.AppendLine();
            sb.AppendLine("Use these routine patterns to:");
            sb.AppendLine("- Warn about potential scheduling conflicts when user creates new habits");
            sb.AppendLine("- Suggest optimal time slots when asked");
            sb.AppendLine("- Personalize scheduling advice based on detected patterns");
        }
        return sb.ToString();
    }
}
